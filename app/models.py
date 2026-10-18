from datetime import datetime
from sqlalchemy import Column, Integer, BigInteger, String, Boolean, DateTime, ForeignKey, Text
from sqlalchemy.orm import relationship
from app.database import Base

class User(Base):
    __tablename__ = "users"

    id = Column(Integer, primary_key=True, index=True)
    telegram_id = Column(BigInteger, unique=True, index=True, nullable=False)
    username = Column(String(128), nullable=True)
    first_name = Column(String(128), nullable=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)

    licenses = relationship("License", back_populates="user", cascade="all, delete-orphan")

class License(Base):
    __tablename__ = "licenses"

    id = Column(Integer, primary_key=True, index=True)
    license_key = Column(String(64), unique=True, index=True, nullable=False)
    user_id = Column(Integer, ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True)
    status = Column(String(32), default="active", nullable=False) # active, revoked, expired
    max_devices = Column(Integer, default=1, nullable=False)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)
    expires_at = Column(DateTime, nullable=False)
    notes = Column(Text, nullable=True)

    user = relationship("User", back_populates="licenses")
    devices = relationship("Device", back_populates="license", cascade="all, delete-orphan")
    logs = relationship("ActivationLog", back_populates="license", cascade="all, delete-orphan")

class Device(Base):
    __tablename__ = "devices"

    id = Column(Integer, primary_key=True, index=True)
    license_id = Column(Integer, ForeignKey("licenses.id", ondelete="CASCADE"), nullable=False, index=True)
    hwid = Column(String(128), nullable=False, index=True)
    device_name = Column(String(128), default="PC", nullable=False)
    is_active = Column(Boolean, default=True, nullable=False)
    first_seen = Column(DateTime, default=datetime.utcnow, nullable=False)
    last_seen = Column(DateTime, default=datetime.utcnow, nullable=False)

    license = relationship("License", back_populates="devices")

class ActivationLog(Base):
    __tablename__ = "activation_logs"

    id = Column(Integer, primary_key=True, index=True)
    license_id = Column(Integer, ForeignKey("licenses.id", ondelete="SET NULL"), nullable=True, index=True)
    hwid = Column(String(128), nullable=False)
    action = Column(String(32), nullable=False) # activate, validate, deactivate, transfer
    ip_address = Column(String(64), nullable=True)
    success = Column(Boolean, default=True, nullable=False)
    details = Column(Text, nullable=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)

    license = relationship("License", back_populates="logs")
