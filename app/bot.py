import logging
from datetime import datetime, timedelta
from typing import Optional

from aiogram import Bot, Dispatcher, Router, F
from aiogram.types import Message, ReplyKeyboardMarkup, KeyboardButton, InlineKeyboardMarkup, InlineKeyboardButton
from aiogram.filters import CommandStart, Command
from sqlalchemy import select, func

from app.config import settings
from app.database import AsyncSessionLocal
from app.models import User, License, Device
from app.api import generate_key_string

logger = logging.getLogger("telegram_bot")
router = Router()

def escape_md(text: Optional[str]) -> str:
    if not text:
        return ""
    for ch in ["_", "*", "`", "["]:
        text = text.replace(ch, f"\\{ch}")
    return text

def get_main_keyboard() -> ReplyKeyboardMarkup:
    return ReplyKeyboardMarkup(
        keyboard=[
            [KeyboardButton(text="🔑 Моя лицензия"), KeyboardButton(text="💳 Купить лицензию")],
            [KeyboardButton(text="💻 Мои устройства"), KeyboardButton(text="🔄 Перенести лицензию")],
            [KeyboardButton(text="⚡ Как активировать"), KeyboardButton(text="💬 Поддержка")]
        ],
        resize_keyboard=True
    )

@router.message(CommandStart())
async def cmd_start(message: Message):
    tg_id = message.from_user.id
    username = message.from_user.username
    first_name = message.from_user.first_name

    async with AsyncSessionLocal() as db:
        stmt = select(User).where(User.telegram_id == tg_id)
        res = await db.execute(stmt)
        user = res.scalar_one_or_none()

        if not user:
            user = User(telegram_id=tg_id, username=username, first_name=first_name)
            db.add(user)
            await db.commit()
        else:
            user.username = username
            user.first_name = first_name
            await db.commit()

    safe_name = escape_md(first_name) or "пользователь"
    welcome_text = (
        f"👋 Привет, {safe_name}!\n\n"
        "Добро пожаловать в центр управления лицензиями **WinCleaner**.\n\n"
        "Используйте кнопки меню ниже для управления вашими ключами и устройствами."
    )
    await message.answer(welcome_text, reply_markup=get_main_keyboard(), parse_mode="Markdown")

@router.message(F.text == "🔑 Моя лицензия")
async def handle_my_license(message: Message):
    tg_id = message.from_user.id

    async with AsyncSessionLocal() as db:
        stmt = select(User).where(User.telegram_id == tg_id)
        res = await db.execute(stmt)
        user = res.scalar_one_or_none()

        if not user:
            await message.answer("Вы еще не зарегистрированы. Нажмите /start.")
            return

        lic_stmt = select(License).where(License.user_id == user.id).order_by(License.id.desc())
        lic_res = await db.execute(lic_stmt)
        licenses = lic_res.scalars().all()

        if not licenses:
            text = (
                "❌ У вас пока нет активных лицензий.\n\n"
                "Нажмите **«💳 Купить лицензию»**, чтобы приобрести доступ."
            )
            await message.answer(text, parse_mode="Markdown")
            return

        reply_parts = ["📋 **Ваши лицензии WinCleaner:**\n"]
        for lic in licenses:
            dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
            dev_res = await db.execute(dev_stmt)
            devs = dev_res.scalars().all()

            status_icon = "🟢" if lic.status == "active" and lic.expires_at > datetime.utcnow() else "🔴"
            exp_str = lic.expires_at.strftime("%d.%m.%Y")
            reply_parts.append(
                f"{status_icon} **Ключ:** `{lic.license_key}`\n"
                f"   • Статус: `{lic.status}`\n"
                f"   • Действует до: `{exp_str}`\n"
                f"   • Устройств: `{len(devs)}/{lic.max_devices}`\n"
            )

        await message.answer("\n".join(reply_parts), parse_mode="Markdown")

@router.message(F.text == "💳 Купить лицензию")
async def handle_buy_license(message: Message):
    text = (
        "💳 **Тарифы на лицензию WinCleaner:**\n\n"
        "• **1 месяц** — 299 ₽ (1 ПК)\n"
        "• **1 год** — 990 ₽ (1 ПК)\n"
        "• **Навсегда (Lifetime)** — 1 990 ₽ (2 ПК)\n\n"
        "Для приобретения напишите администратору или выберите способ оплаты через поддержку."
    )
    admin_user = settings.ADMIN_USERNAME.lstrip("@")
    admin_url = f"https://t.me/{admin_user}" if admin_user else "https://t.me/"
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="💬 Написать администратору", url=admin_url)]
    ])
    await message.answer(text, reply_markup=kb, parse_mode="Markdown")

@router.message(F.text == "⚡ Как активировать")
async def handle_how_to_activate(message: Message):
    text = (
        "⚡ **Инструкция по активации WinCleaner:**\n\n"
        "1. Запустите файл `WinCleaner.exe`.\n"
        "2. В появившемся окне активации вставьте ваш лицензионный ключ вида:\n"
        "   `WINC-XXXX-XXXX-XXXX`\n"
        "3. Нажмите кнопку **«⚡ Активировать»**.\n"
        "4. Программа автоматически привяжет текущий HWID вашего ПК к серверу и разблокирует весь функционал."
    )
    await message.answer(text, parse_mode="Markdown")

@router.message(F.text == "💻 Мои устройства")
async def handle_my_devices(message: Message):
    tg_id = message.from_user.id

    async with AsyncSessionLocal() as db:
        u_stmt = select(User).where(User.telegram_id == tg_id)
        u_res = await db.execute(u_stmt)
        user = u_res.scalar_one_or_none()

        if not user:
            await message.answer("Сначала нажмите /start.")
            return

        stmt = select(Device).join(License).where(License.user_id == user.id, Device.is_active == True)
        res = await db.execute(stmt)
        devices = res.scalars().all()

        if not devices:
            await message.answer("На ваших лицензиях пока нет активных привязанных устройств.")
            return

        text_lines = ["💻 **Привязанные устройства:**\n"]
        for d in devices:
            safe_dev_name = escape_md(d.device_name)
            text_lines.append(
                f"• **{safe_dev_name}**\n"
                f"  HWID: `{d.hwid[:16]}...`\n"
                f"  Последняя активность: `{d.last_seen.strftime('%d.%m.%Y %H:%M')}` UTC\n"
            )

        await message.answer("\n".join(text_lines), parse_mode="Markdown")

@router.message(F.text == "🔄 Перенести лицензию")
async def handle_transfer_license(message: Message):
    tg_id = message.from_user.id

    async with AsyncSessionLocal() as db:
        u_stmt = select(User).where(User.telegram_id == tg_id)
        u_res = await db.execute(u_stmt)
        user = u_res.scalar_one_or_none()

        if not user:
            await message.answer("Сначала нажмите /start.")
            return

        lic_stmt = select(License).where(License.user_id == user.id, License.status == "active")
        lic_res = await db.execute(lic_stmt)
        licenses = lic_res.scalars().all()

        if not licenses:
            await message.answer("У вас нет активных лицензий для переноса.")
            return

        # Unbind all active devices for the user's active licenses
        unbound_count = 0
        for lic in licenses:
            dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
            dev_res = await db.execute(dev_stmt)
            for d in dev_res.scalars().all():
                d.is_active = False
                unbound_count += 1

        await db.commit()

        if unbound_count > 0:
            await message.answer(
                f"✅ Успешно отвязано устройств: {unbound_count}.\n\n"
                "Теперь вы можете активировать ваш ключ на новом компьютере!",
                parse_mode="Markdown"
            )
        else:
            await message.answer("На ваших лицензиях не было привязанных устройств. Ключ уже свободен для ввода.")

@router.message(F.text == "💬 Поддержка")
async def handle_support(message: Message):
    admin_user = settings.ADMIN_USERNAME.lstrip("@")
    admin_contact = f"@{admin_user}" if admin_user else "администратору"
    admin_url = f"https://t.me/{admin_user}" if admin_user else "https://t.me/"
    text = (
        "💬 **Служба поддержки WinCleaner**\n\n"
        "Если у вас возникли технические вопросы, проблемы с активацией или сменой оборудования, обратитесь к администратору:\n"
        f"👉 {admin_contact}"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="💬 Написать в поддержку", url=admin_url)]
    ])
    await message.answer(text, reply_markup=kb, parse_mode="Markdown")

# ===========================================================================
# Admin Commands
# ===========================================================================

def is_admin(user_id: int) -> bool:
    return user_id in settings.admin_ids

@router.message(Command("genkey"))
async def cmd_genkey(message: Message):
    if not is_admin(message.from_user.id):
        return

    # Syntax: /genkey <days> [tg_id] [max_devices]
    parts = message.text.split()
    days = 30
    target_tg_id = None
    max_devices = 1

    if len(parts) > 1 and parts[1].isdigit():
        days = int(parts[1])
    if len(parts) > 2 and parts[2].isdigit():
        target_tg_id = int(parts[2])
    if len(parts) > 3 and parts[3].isdigit():
        max_devices = int(parts[3])

    key = generate_key_string()
    now = datetime.utcnow()
    exp = now + timedelta(days=days)

    async with AsyncSessionLocal() as db:
        user_id = None
        if target_tg_id:
            u_stmt = select(User).where(User.telegram_id == target_tg_id)
            u_res = await db.execute(u_stmt)
            user = u_res.scalar_one_or_none()
            if not user:
                user = User(telegram_id=target_tg_id)
                db.add(user)
                await db.flush()
            user_id = user.id

        lic = License(
            license_key=key,
            user_id=user_id,
            status="active",
            max_devices=max_devices,
            created_at=now,
            expires_at=exp,
            notes=f"Issued by admin {message.from_user.id}"
        )
        db.add(lic)
        await db.commit()

    reply = (
        f"✅ **Ключ успешно создан:**\n\n"
        f"🔑 `{key}`\n"
        f"📅 Срок: `{days}` дней (до {exp.strftime('%d.%m.%Y')})\n"
        f"💻 Макс. устройств: `{max_devices}`\n"
        f"👤 Привязан к TG ID: `{target_tg_id or 'Любой пользователь'}`"
    )
    await message.answer(reply, parse_mode="Markdown")

@router.message(Command("revoke"))
async def cmd_revoke(message: Message):
    if not is_admin(message.from_user.id):
        return

    parts = message.text.split()
    if len(parts) < 2:
        await message.answer("Использование: `/revoke <KEY>`")
        return

    key = parts[1].strip().upper()

    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await message.answer("Ключ не найден.")
            return

        lic.status = "revoked"
        dev_stmt = select(Device).where(Device.license_id == lic.id)
        dev_res = await db.execute(dev_stmt)
        for d in dev_res.scalars().all():
            d.is_active = False

        await db.commit()

    await message.answer(f"🚫 Ключ `{key}` успешно отозван.", parse_mode="Markdown")

@router.message(Command("stats"))
async def cmd_stats(message: Message):
    if not is_admin(message.from_user.id):
        return

    async with AsyncSessionLocal() as db:
        total_users = (await db.execute(select(func.count(User.id)))).scalar() or 0
        total_licenses = (await db.execute(select(func.count(License.id)))).scalar() or 0
        active_licenses = (await db.execute(select(func.count(License.id)).where(License.status == "active"))).scalar() or 0
        active_devices = (await db.execute(select(func.count(Device.id)).where(Device.is_active == True))).scalar() or 0

    stats_text = (
        "📊 **Статистика системы лицензирования:**\n\n"
        f"👥 Всего пользователей: `{total_users}`\n"
        f"🔑 Всего лицензий: `{total_licenses}`\n"
        f"🟢 Активных лицензий: `{active_licenses}`\n"
        f"💻 Активных устройств: `{active_devices}`"
    )
    await message.answer(stats_text, parse_mode="Markdown")

@router.message()
async def handle_unknown_message(message: Message):
    text = (
        "❓ Не удалось распознать команду.\n\n"
        "Пожалуйста, используйте кнопки меню ниже для управления лицензиями WinCleaner:"
    )
    await message.answer(text, reply_markup=get_main_keyboard())
