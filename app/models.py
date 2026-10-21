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
    is_banned = Column(Boolean, default=False, nullable=False)
    balance = Column(Integer, default=0, nullable=False) # In RUB (from referrals/cashback)
    referrer_id = Column(Integer, ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True)
    referral_code = Column(String(32), unique=True, nullable=True, index=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)

    licenses = relationship("License", back_populates="user", cascade="all, delete-orphan")
    tickets = relationship("SupportTicket", back_populates="user", cascade="all, delete-orphan")
    orders = relationship("Order", back_populates="user", cascade="all, delete-orphan")
    referrer = relationship("User", remote_side=[id], backref="referrals")

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
    reminder_3d_sent = Column(Boolean, default=False, nullable=False)
    reminder_1d_sent = Column(Boolean, default=False, nullable=False)
    expired_sent = Column(Boolean, default=False, nullable=False)

    user = relationship("User", back_populates="licenses")
    devices = relationship("Device", back_populates="license", cascade="all, delete-orphan")
    logs = relationship("ActivationLog", back_populates="license", cascade="all, delete-orphan")

class Device(Base):
    __tablename__ = "devices"

    id = Column(Integer, primary_key=True, index=True)
    license_id = Column(Integer, ForeignKey("licenses.id", ondelete="CASCADE"), nullable=False, index=True)
    hwid = Column(String(128), nullable=False, index=True)
    device_name = Column(String(128), default="PC", nullable=False)
    os_version = Column(String(128), default="Windows 10/11", nullable=False)
    app_version = Column(String(32), default="2.0.0", nullable=False)
    ip_address = Column(String(64), nullable=True)
    is_active = Column(Boolean, default=True, nullable=False)
    first_seen = Column(DateTime, default=datetime.utcnow, nullable=False)
    last_seen = Column(DateTime, default=datetime.utcnow, nullable=False)

    license = relationship("License", back_populates="devices")

class ActivationLog(Base):
    __tablename__ = "activation_logs"

    id = Column(Integer, primary_key=True, index=True)
    license_id = Column(Integer, ForeignKey("licenses.id", ondelete="SET NULL"), nullable=True, index=True)
    hwid = Column(String(128), nullable=False)
    action = Column(String(32), nullable=False) # activate, validate, deactivate, transfer, admin_unbind, admin_revoke, admin_extend
    ip_address = Column(String(64), nullable=True)
    success = Column(Boolean, default=True, nullable=False)
    details = Column(Text, nullable=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)

    license = relationship("License", back_populates="logs")

class SupportTicket(Base):
    __tablename__ = "support_tickets"

    id = Column(Integer, primary_key=True, index=True)
    user_id = Column(Integer, ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True)
    user_telegram_id = Column(BigInteger, index=True, nullable=False)
    status = Column(String(32), default="open", nullable=False) # open, answered, closed
    ticket_type = Column(String(32), default="support", nullable=False) # support, purchase
    subject = Column(String(255), nullable=True)
    last_admin_message_id = Column(BigInteger, nullable=True, index=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)
    updated_at = Column(DateTime, default=datetime.utcnow, onupdate=datetime.utcnow, nullable=False)

    user = relationship("User", back_populates="tickets")
    messages = relationship("TicketMessage", back_populates="ticket", cascade="all, delete-orphan")

class TicketMessage(Base):
    __tablename__ = "ticket_messages"

    id = Column(Integer, primary_key=True, index=True)
    ticket_id = Column(Integer, ForeignKey("support_tickets.id", ondelete="CASCADE"), nullable=False, index=True)
    sender_type = Column(String(16), nullable=False) # "user", "admin"
    sender_telegram_id = Column(BigInteger, nullable=True)
    text = Column(Text, nullable=True)
    has_media = Column(Boolean, default=False, nullable=False)
    media_type = Column(String(32), nullable=True) # photo, document, voice
    file_id = Column(String(256), nullable=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)

    ticket = relationship("SupportTicket", back_populates="messages")

class PromoCode(Base):
    __tablename__ = "promo_codes"

    id = Column(Integer, primary_key=True, index=True)
    code = Column(String(64), unique=True, index=True, nullable=False)
    discount_percent = Column(Integer, default=10, nullable=False) # Percentage (e.g. 10, 20, 50)
    max_uses = Column(Integer, default=100, nullable=False)
    current_uses = Column(Integer, default=0, nullable=False)
    valid_until = Column(DateTime, nullable=True)
    is_active = Column(Boolean, default=True, nullable=False)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)

class Order(Base):
    __tablename__ = "orders"

    id = Column(Integer, primary_key=True, index=True)
    order_id = Column(String(64), unique=True, index=True, nullable=False)
    user_id = Column(Integer, ForeignKey("users.id", ondelete="SET NULL"), nullable=True, index=True)
    user_telegram_id = Column(BigInteger, index=True, nullable=False)
    item_type = Column(String(32), nullable=False) # month, year, lifetime, hwid
    amount_rub = Column(Integer, nullable=False)
    stars_amount = Column(Integer, default=0, nullable=False)
    payment_method = Column(String(32), default="stars", nullable=False) # stars, cryptobot, sbp, balance
    status = Column(String(32), default="pending", nullable=False) # pending, paid, cancelled
    promo_code_id = Column(Integer, ForeignKey("promo_codes.id", ondelete="SET NULL"), nullable=True)
    result_license_key = Column(String(64), nullable=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)
    paid_at = Column(DateTime, nullable=True)

    user = relationship("User", back_populates="orders")
    promo = relationship("PromoCode")

class ReferralHistory(Base):
    __tablename__ = "referral_history"

    id = Column(Integer, primary_key=True, index=True)
    referrer_id = Column(Integer, ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True)
    referred_id = Column(Integer, ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True)
    reward_rub = Column(Integer, default=0, nullable=False)
    order_id = Column(Integer, ForeignKey("orders.id", ondelete="SET NULL"), nullable=True)
    created_at = Column(DateTime, default=datetime.utcnow, nullable=False)
