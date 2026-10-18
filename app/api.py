import time
import secrets
import string
from collections import defaultdict
from datetime import datetime, timedelta
from typing import Optional

from fastapi import APIRouter, Depends, HTTPException, Request, Header, status
from sqlalchemy.ext.asyncio import AsyncSession
from sqlalchemy import select, func

from app.config import settings
from app.database import get_db
from app.models import User, License, Device, ActivationLog
from app.schemas import (
    ActivationRequest, ActivationResponse,
    ValidateRequest, ValidateResponse,
    DeactivateRequest, DeactivateResponse,
    AdminGenerateKeyRequest, AdminRevokeRequest, AdminExtendRequest
)
from app.crypto import sign_license

router = APIRouter(prefix="/api/v1", tags=["licensing"])

# Simple in-memory rate limiting: IP -> list of timestamps
_rate_limits = defaultdict(list)
RATE_LIMIT_WINDOW = 60 # seconds
RATE_LIMIT_MAX_REQUESTS = 30 # requests per minute per IP

def check_rate_limit(request: Request):
    client_ip = request.client.host if request.client else "unknown"
    now = time.time()
    timestamps = _rate_limits[client_ip]
    # Prune old timestamps
    _rate_limits[client_ip] = [t for t in timestamps if now - t < RATE_LIMIT_WINDOW]
    if len(_rate_limits[client_ip]) >= RATE_LIMIT_MAX_REQUESTS:
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail="Слишком много запросов. Пожалуйста, подождите минуту."
        )
    _rate_limits[client_ip].append(now)

def verify_admin_token(x_admin_token: Optional[str] = Header(None)):
    if not x_admin_token or x_admin_token.strip() != settings.ADMIN_SECRET:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Неверный токен администратора."
        )

def generate_key_string() -> str:
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    parts = ["WINC"]
    for _ in range(3):
        parts.append("".join(secrets.choice(alphabet) for _ in range(4)))
    return "-".join(parts)

@router.get("/health")
async def health_check():
    return {
        "status": "ok",
        "service": "wincleaner-licensing",
        "server_time": datetime.utcnow().isoformat() + "Z",
        "public_key": settings.ED25519_PUBLIC_KEY_HEX
    }

@router.post("/activate", response_model=ActivationResponse)
async def activate_license(
    req: ActivationRequest,
    request: Request,
    db: AsyncSession = Depends(get_db)
):
    check_rate_limit(request)
    client_ip = request.client.host if request.client else "unknown"
    key = req.license_key.strip().upper()
    hwid = req.hwid.strip().upper()

    stmt = select(License).where(License.license_key == key)
    res = await db.execute(stmt)
    lic = res.scalar_one_or_none()

    if not lic:
        log = ActivationLog(license_id=None, hwid=hwid, action="activate", ip_address=client_ip, success=False, details="Ключ не найден")
        db.add(log)
        await db.commit()
        raise HTTPException(status_code=404, detail="Лицензионный ключ не найден. Проверьте правильность ввода.")

    if lic.status == "revoked":
        log = ActivationLog(license_id=lic.id, hwid=hwid, action="activate", ip_address=client_ip, success=False, details="Лицензия отозвана")
        db.add(log)
        await db.commit()
        raise HTTPException(status_code=403, detail="Данная лицензия отозвана администратором.")

    now = datetime.utcnow()
    if now > lic.expires_at:
        lic.status = "expired"
        await db.commit()
        log = ActivationLog(license_id=lic.id, hwid=hwid, action="activate", ip_address=client_ip, success=False, details="Срок действия истек")
        db.add(log)
        await db.commit()
        raise HTTPException(status_code=403, detail=f"Срок действия лицензии истек {lic.expires_at.strftime('%d.%m.%Y')}.")

    # Check active devices for this license
    dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
    dev_res = await db.execute(dev_stmt)
    active_devices = dev_res.scalars().all()

    existing_dev = next((d for d in active_devices if d.hwid == hwid), None)
    if existing_dev:
        existing_dev.last_seen = now
        existing_dev.device_name = req.device_name or existing_dev.device_name
    else:
        if len(active_devices) >= lic.max_devices:
            log = ActivationLog(
                license_id=lic.id, hwid=hwid, action="activate", ip_address=client_ip,
                success=False, details=f"Превышен лимит устройств ({len(active_devices)}/{lic.max_devices})"
            )
            db.add(log)
            await db.commit()
            raise HTTPException(
                status_code=403,
                detail=f"Лимит устройств для этой лицензии исчерпан ({lic.max_devices} ПК). Отвяжите старое устройство в Telegram-боте."
            )

        new_dev = Device(
            license_id=lic.id,
            hwid=hwid,
            device_name=req.device_name or "PC",
            is_active=True,
            first_seen=now,
            last_seen=now
        )
        db.add(new_dev)

    log = ActivationLog(license_id=lic.id, hwid=hwid, action="activate", ip_address=client_ip, success=True, details="Успешная активация")
    db.add(log)
    await db.commit()

    signature = sign_license(lic.license_key, hwid, lic.expires_at)

    return ActivationResponse(
        status="active",
        license_key=lic.license_key,
        hwid=hwid,
        expires_at=lic.expires_at.strftime("%Y-%m-%dT%H:%M:%SZ"),
        server_time=now.strftime("%Y-%m-%dT%H:%M:%SZ"),
        signature=signature
    )

@router.post("/validate", response_model=ValidateResponse)
async def validate_license(
    req: ValidateRequest,
    request: Request,
    db: AsyncSession = Depends(get_db)
):
    check_rate_limit(request)
    client_ip = request.client.host if request.client else "unknown"
    key = req.license_key.strip().upper()
    hwid = req.hwid.strip().upper()

    stmt = select(License).where(License.license_key == key)
    res = await db.execute(stmt)
    lic = res.scalar_one_or_none()

    if not lic or lic.status != "active":
        return ValidateResponse(
            status=lic.status if lic else "not_found",
            license_key=key,
            hwid=hwid,
            expires_at="",
            signature=""
        )

    now = datetime.utcnow()
    if now > lic.expires_at:
        lic.status = "expired"
        await db.commit()
        return ValidateResponse(
            status="expired",
            license_key=key,
            hwid=hwid,
            expires_at=lic.expires_at.strftime("%Y-%m-%dT%H:%M:%SZ"),
            signature=""
        )

    # Check device binding
    dev_stmt = select(Device).where(Device.license_id == lic.id, Device.hwid == hwid, Device.is_active == True)
    dev_res = await db.execute(dev_stmt)
    dev = dev_res.scalar_one_or_none()

    if not dev:
        return ValidateResponse(
            status="device_not_linked",
            license_key=key,
            hwid=hwid,
            expires_at=lic.expires_at.strftime("%Y-%m-%dT%H:%M:%SZ"),
            signature=""
        )

    dev.last_seen = now
    await db.commit()

    sig = sign_license(lic.license_key, hwid, lic.expires_at)

    return ValidateResponse(
        status="active",
        license_key=lic.license_key,
        hwid=hwid,
        expires_at=lic.expires_at.strftime("%Y-%m-%dT%H:%M:%SZ"),
        signature=sig
    )

@router.post("/deactivate", response_model=DeactivateResponse)
async def deactivate_device(
    req: DeactivateRequest,
    request: Request,
    db: AsyncSession = Depends(get_db)
):
    check_rate_limit(request)
    client_ip = request.client.host if request.client else "unknown"
    key = req.license_key.strip().upper()
    hwid = req.hwid.strip().upper()

    stmt = select(License).where(License.license_key == key)
    res = await db.execute(stmt)
    lic = res.scalar_one_or_none()

    if not lic:
        raise HTTPException(status_code=404, detail="Лицензия не найдена.")

    dev_stmt = select(Device).where(Device.license_id == lic.id, Device.hwid == hwid, Device.is_active == True)
    dev_res = await db.execute(dev_stmt)
    dev = dev_res.scalar_one_or_none()

    if dev:
        dev.is_active = False
        log = ActivationLog(license_id=lic.id, hwid=hwid, action="deactivate", ip_address=client_ip, success=True, details="Устройство отвязано")
        db.add(log)
        await db.commit()
        return DeactivateResponse(status="success", message="Устройство успешно отвязано от лицензии.")

    return DeactivateResponse(status="not_found", message="Устройство не было привязано.")

@router.post("/admin/generate")
async def admin_generate_key(
    req: AdminGenerateKeyRequest,
    db: AsyncSession = Depends(get_db),
    _token = Depends(verify_admin_token)
):
    now = datetime.utcnow()
    exp = now + timedelta(days=req.days)
    key = generate_key_string()

    user_id = None
    if req.telegram_id:
        u_stmt = select(User).where(User.telegram_id == req.telegram_id)
        u_res = await db.execute(u_stmt)
        user = u_res.scalar_one_or_none()
        if not user:
            user = User(telegram_id=req.telegram_id)
            db.add(user)
            await db.flush()
        user_id = user.id

    lic = License(
        license_key=key,
        user_id=user_id,
        status="active",
        max_devices=req.max_devices,
        created_at=now,
        expires_at=exp,
        notes=req.notes
    )
    db.add(lic)
    await db.commit()

    return {
        "status": "success",
        "license_key": key,
        "days": req.days,
        "expires_at": exp.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "max_devices": req.max_devices,
        "telegram_id": req.telegram_id
    }

@router.post("/admin/revoke")
async def admin_revoke_key(
    req: AdminRevokeRequest,
    db: AsyncSession = Depends(get_db),
    _token = Depends(verify_admin_token)
):
    key = req.license_key.strip().upper()
    stmt = select(License).where(License.license_key == key)
    res = await db.execute(stmt)
    lic = res.scalar_one_or_none()

    if not lic:
        raise HTTPException(status_code=404, detail="Ключ не найден.")

    lic.status = "revoked"
    lic.notes = (lic.notes or "") + f" | Revoked: {req.reason}"

    # Deactivate all linked devices
    dev_stmt = select(Device).where(Device.license_id == lic.id)
    dev_res = await db.execute(dev_stmt)
    for d in dev_res.scalars().all():
        d.is_active = False

    await db.commit()
    return {"status": "success", "message": f"Лицензия {key} отозвана."}

@router.post("/admin/extend")
async def admin_extend_key(
    req: AdminExtendRequest,
    db: AsyncSession = Depends(get_db),
    _token = Depends(verify_admin_token)
):
    key = req.license_key.strip().upper()
    stmt = select(License).where(License.license_key == key)
    res = await db.execute(stmt)
    lic = res.scalar_one_or_none()

    if not lic:
        raise HTTPException(status_code=404, detail="Ключ не найден.")

    base_time = max(datetime.utcnow(), lic.expires_at)
    lic.expires_at = base_time + timedelta(days=req.extra_days)
    if lic.status == "expired":
        lic.status = "active"

    await db.commit()
    return {
        "status": "success",
        "license_key": key,
        "new_expires_at": lic.expires_at.strftime("%Y-%m-%dT%H:%M:%SZ")
    }
