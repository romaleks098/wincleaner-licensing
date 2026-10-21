from datetime import datetime
from typing import Optional, List
from pydantic import BaseModel, Field

class ActivationRequest(BaseModel):
    license_key: str = Field(..., description="License Key in format WINC-XXXX-XXXX-XXXX")
    hwid: str = Field(..., description="Unique client machine HWID (SHA-256 hex)")
    device_name: Optional[str] = Field("PC", description="Friendly device hostname")
    os_version: Optional[str] = Field("Windows 10/11", description="Operating system details")
    app_version: Optional[str] = Field("2.0.0", description="Client software build version")

class ActivationResponse(BaseModel):
    status: str
    license_key: str
    hwid: str
    expires_at: str
    server_time: str
    signature: str

class ValidateRequest(BaseModel):
    license_key: str
    hwid: str
    os_version: Optional[str] = "Windows 10/11"
    app_version: Optional[str] = "2.0.0"

class ValidateResponse(BaseModel):
    status: str
    license_key: str
    hwid: str
    expires_at: str
    signature: str

class DeactivateRequest(BaseModel):
    license_key: str
    hwid: str

class DeactivateResponse(BaseModel):
    status: str
    message: str

class AdminGenerateKeyRequest(BaseModel):
    days: int = Field(30, ge=1, le=3650)
    max_devices: int = Field(1, ge=1, le=10)
    telegram_id: Optional[int] = None
    notes: Optional[str] = None

class AdminRevokeRequest(BaseModel):
    license_key: str
    reason: Optional[str] = "Revoked by admin"

class AdminExtendRequest(BaseModel):
    license_key: str
    extra_days: int = Field(30, ge=1, le=3650)
