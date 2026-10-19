import logging
from datetime import datetime, timedelta
from typing import Optional

from aiogram import Bot, Dispatcher, Router, F
from aiogram.types import Message, ReplyKeyboardMarkup, KeyboardButton, InlineKeyboardMarkup, InlineKeyboardButton
from aiogram.filters import CommandStart, Command
from sqlalchemy import select, func, desc

from app.config import settings
from app.database import AsyncSessionLocal
from app.models import User, License, Device, ActivationLog
from app.crypto import generate_key_string

logger = logging.getLogger("telegram_bot")
router = Router()

_bot_instance: Optional[Bot] = None

def set_bot_instance(b: Bot):
    global _bot_instance
    _bot_instance = b

def get_bot_instance() -> Optional[Bot]:
    global _bot_instance
    return _bot_instance

async def send_admin_alert(html_text: str):
    """
    Sends real-time security alerts to all configured administrator Telegram IDs.
    """
    bot = get_bot_instance()
    if not bot:
        return
    for admin_id in settings.admin_ids:
        try:
            await bot.send_message(admin_id, html_text, parse_mode="HTML")
        except Exception as e:
            logger.warning(f"Failed to send admin alert to {admin_id}: {e}")

def escape_md(text: Optional[str]) -> str:
    if not text:
        return ""
    for ch in ["_", "*", "`", "["]:
        text = text.replace(ch, f"\\{ch}")
    return text

def escape_html(text: Optional[str]) -> str:
    if not text:
        return ""
    return str(text).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

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
# Admin Commands & Security Management
# ===========================================================================

def is_admin(user_id: int) -> bool:
    return user_id in settings.admin_ids

@router.message(Command("admin"))
@router.message(Command("help"))
async def cmd_admin_help(message: Message):
    if not is_admin(message.from_user.id):
        return

    help_text = (
        "🛡️ <b>Панель управления лицензиями WinCleaner</b>\n\n"
        "<b>Команды администратора:</b>\n"
        "• <code>/genkey &lt;дни&gt; [tg_id] [устройств]</code> — Выпустить новый ключ\n"
        "• <code>/info &lt;ключ&gt;</code> — Полная информация о ключе и привязанных ПК\n"
        "• <code>/unbind &lt;ключ&gt;</code> — Сбросить привязку всех устройств ключа\n"
        "• <code>/extend &lt;ключ&gt; &lt;дни&gt;</code> — Продлить срок действия на N дней\n"
        "• <code>/revoke &lt;ключ&gt;</code> — Отозвать ключ и аннулировать устройства\n"
        "• <code>/devices &lt;ключ&gt;</code> — Список активных устройств ключа\n"
        "• <code>/logs</code> — Последние 10 логов безопасности\n"
        "• <code>/stats</code> — Общая статистика пользователей и лицензий"
    )
    await message.answer(help_text, parse_mode="HTML")

@router.message(Command("genkey"))
async def cmd_genkey(message: Message):
    if not is_admin(message.from_user.id):
        return

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
        f"✅ <b>Ключ успешно создан:</b>\n\n"
        f"🔑 <code>{key}</code>\n"
        f"📅 Срок: <b>{days} дней</b> (до {exp.strftime('%d.%m.%Y')})\n"
        f"💻 Макс. устройств: <b>{max_devices}</b>\n"
        f"👤 Привязан к TG ID: <code>{target_tg_id or 'Любой пользователь'}</code>"
    )
    await message.answer(reply, parse_mode="HTML")

@router.message(Command("info"))
async def cmd_info(message: Message):
    if not is_admin(message.from_user.id):
        return

    parts = message.text.split()
    if len(parts) < 2:
        await message.answer("Использование: <code>/info &lt;KEY&gt;</code>", parse_mode="HTML")
        return

    key = parts[1].strip().upper()

    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await message.answer("❌ Ключ не найден в базе данных.")
            return

        dev_stmt = select(Device).where(Device.license_id == lic.id)
        dev_res = await db.execute(dev_stmt)
        all_devs = dev_res.scalars().all()

        user_info = "Не привязан"
        if lic.user_id:
            u_stmt = select(User).where(User.id == lic.user_id)
            user_res = await db.execute(u_stmt)
            u = user_res.scalar_one_or_none()
            if u:
                user_info = f"@{u.username} (ID: <code>{u.telegram_id}</code>)" if u.username else f"ID: <code>{u.telegram_id}</code>"

        status_icon = "🟢" if lic.status == "active" and lic.expires_at > datetime.utcnow() else "🔴"
        active_count = sum(1 for d in all_devs if d.is_active)

        dev_lines = []
        for d in all_devs:
            status_dot = "🟢" if d.is_active else "⚪"
            dev_lines.append(
                f"  {status_dot} <b>{escape_html(d.device_name)}</b>\n"
                f"     HWID: <code>{d.hwid[:16]}...</code>\n"
                f"     Активен: {d.last_seen.strftime('%d.%m.%Y %H:%M')} UTC"
            )

        dev_block = "\n".join(dev_lines) if dev_lines else "  Нет привязанных устройств"

        info_text = (
            f"🔍 <b>Информация о лицензии:</b>\n\n"
            f"🔑 Ключ: <code>{lic.license_key}</code>\n"
            f"{status_icon} Статус: <b>{lic.status}</b>\n"
            f"📅 Создан: {lic.created_at.strftime('%d.%m.%Y')}\n"
            f"⏳ Действует до: <b>{lic.expires_at.strftime('%d.%m.%Y %H:%M')} UTC</b>\n"
            f"💻 Устройств: <b>{active_count}/{lic.max_devices}</b>\n"
            f"👤 Владелец: {user_info}\n"
            f"📝 Заметки: <i>{escape_html(lic.notes or 'нет')}</i>\n\n"
            f"<b>Устройства:</b>\n{dev_block}"
        )
        await message.answer(info_text, parse_mode="HTML")

@router.message(Command("unbind"))
async def cmd_unbind(message: Message):
    if not is_admin(message.from_user.id):
        return

    parts = message.text.split()
    if len(parts) < 2:
        await message.answer("Использование: <code>/unbind &lt;KEY&gt;</code>", parse_mode="HTML")
        return

    key = parts[1].strip().upper()

    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await message.answer("❌ Ключ не найден.")
            return

        dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
        dev_res = await db.execute(dev_stmt)
        devs = dev_res.scalars().all()

        unbound_count = len(devs)
        for d in devs:
            d.is_active = False

        log = ActivationLog(
            license_id=lic.id,
            hwid="ADMIN_RESET",
            action="admin_unbind",
            ip_address="telegram_admin",
            success=True,
            details=f"Admin {message.from_user.id} unbound {unbound_count} device(s)"
        )
        db.add(log)
        await db.commit()

    await message.answer(f"✅ Успешно отвязано устройств: <b>{unbound_count}</b> для ключа <code>{key}</code>.", parse_mode="HTML")

@router.message(Command("devices"))
async def cmd_devices(message: Message):
    if not is_admin(message.from_user.id):
        return

    parts = message.text.split()
    if len(parts) < 2:
        await message.answer("Использование: <code>/devices &lt;KEY&gt;</code>", parse_mode="HTML")
        return

    key = parts[1].strip().upper()

    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await message.answer("❌ Ключ не найден.")
            return

        dev_stmt = select(Device).where(Device.license_id == lic.id)
        dev_res = await db.execute(dev_stmt)
        devs = dev_res.scalars().all()

        if not devs:
            await message.answer(f"На ключе <code>{key}</code> нет зарегистрированных устройств.", parse_mode="HTML")
            return

        lines = [f"💻 <b>Устройства для ключа {key}:</b>\n"]
        for d in devs:
            icon = "🟢" if d.is_active else "⚪"
            lines.append(
                f"{icon} <b>{escape_html(d.device_name)}</b> (Активен: {d.is_active})\n"
                f"   HWID: <code>{d.hwid}</code>\n"
                f"   Первый вход: {d.first_seen.strftime('%d.%m.%Y %H:%M')}\n"
                f"   Последний вход: {d.last_seen.strftime('%d.%m.%Y %H:%M')}\n"
            )

        await message.answer("\n".join(lines), parse_mode="HTML")

@router.message(Command("extend"))
async def cmd_extend(message: Message):
    if not is_admin(message.from_user.id):
        return

    parts = message.text.split()
    if len(parts) < 3 or not parts[2].isdigit():
        await message.answer("Использование: <code>/extend &lt;KEY&gt; &lt;days&gt;</code>", parse_mode="HTML")
        return

    key = parts[1].strip().upper()
    days = int(parts[2])

    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await message.answer("❌ Ключ не найден.")
            return

        base_time = lic.expires_at if lic.expires_at > datetime.utcnow() else datetime.utcnow()
        lic.expires_at = base_time + timedelta(days=days)
        if lic.status == "expired":
            lic.status = "active"

        log = ActivationLog(
            license_id=lic.id,
            hwid="ADMIN_EXTEND",
            action="admin_extend",
            ip_address="telegram_admin",
            success=True,
            details=f"Admin {message.from_user.id} extended by {days} days"
        )
        db.add(log)
        await db.commit()

    await message.answer(
        f"✅ Лицензия <code>{key}</code> продлена на <b>{days} дней</b>.\n"
        f"Новый срок: <b>{lic.expires_at.strftime('%d.%m.%Y %H:%M')} UTC</b>",
        parse_mode="HTML"
    )

@router.message(Command("revoke"))
async def cmd_revoke(message: Message):
    if not is_admin(message.from_user.id):
        return

    parts = message.text.split()
    if len(parts) < 2:
        await message.answer("Использование: <code>/revoke &lt;KEY&gt;</code>", parse_mode="HTML")
        return

    key = parts[1].strip().upper()

    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await message.answer("❌ Ключ не найден.")
            return

        lic.status = "revoked"
        dev_stmt = select(Device).where(Device.license_id == lic.id)
        dev_res = await db.execute(dev_stmt)
        for d in dev_res.scalars().all():
            d.is_active = False

        log = ActivationLog(
            license_id=lic.id,
            hwid="ADMIN_REVOKE",
            action="admin_revoke",
            ip_address="telegram_admin",
            success=True,
            details=f"Admin {message.from_user.id} revoked license"
        )
        db.add(log)
        await db.commit()

    await message.answer(f"🚫 Ключ <code>{key}</code> отозван. Все привязанные устройства заблокированы.", parse_mode="HTML")

@router.message(Command("logs"))
async def cmd_logs(message: Message):
    if not is_admin(message.from_user.id):
        return

    async with AsyncSessionLocal() as db:
        stmt = select(ActivationLog).order_by(desc(ActivationLog.id)).limit(10)
        res = await db.execute(stmt)
        logs = res.scalars().all()

        if not logs:
            await message.answer("Журнал безопасности пока пуст.")
            return

        lines = ["📜 <b>Последние 10 событий безопасности:</b>\n"]
        for log in logs:
            icon = "✅" if log.success else "⚠️"
            time_str = log.created_at.strftime("%d.%m %H:%M")
            safe_ip = escape_html(log.ip_address or "unknown")
            safe_det = escape_html(log.details or log.action)
            hwid_cut = log.hwid[:8] + "..." if len(log.hwid) > 8 else log.hwid
            lines.append(f"{icon} <code>{time_str}</code> | <b>{log.action}</b> | IP: <code>{safe_ip}</code>\n   {safe_det} ({hwid_cut})")

        await message.answer("\n".join(lines), parse_mode="HTML")

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
        "📊 <b>Статистика системы лицензирования:</b>\n\n"
        f"👥 Всего пользователей: <code>{total_users}</code>\n"
        f"🔑 Всего лицензий: <code>{total_licenses}</code>\n"
        f"🟢 Активных лицензий: <code>{active_licenses}</code>\n"
        f"💻 Активных устройств: <code>{active_devices}</code>"
    )
    await message.answer(stats_text, parse_mode="HTML")

@router.message()
async def handle_unknown_message(message: Message):
    text = (
        "❓ Не удалось распознать команду.\n\n"
        "Пожалуйста, используйте кнопки меню ниже для управления лицензиями WinCleaner:"
    )
    await message.answer(text, reply_markup=get_main_keyboard())
