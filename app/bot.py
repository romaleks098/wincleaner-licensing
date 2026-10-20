import asyncio
import logging
from datetime import datetime, timedelta, timezone
from typing import Optional, List

from aiogram import Bot, Dispatcher, Router, F
from aiogram.types import (
    Message,
    CallbackQuery,
    ReplyKeyboardMarkup,
    KeyboardButton,
    InlineKeyboardMarkup,
    InlineKeyboardButton,
)
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


def utc_now() -> datetime:
    """Returns timezone-naive UTC datetime for consistent database storage."""
    return datetime.now(timezone.utc).replace(tzinfo=None)


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


def is_admin(user_id: int, username: Optional[str] = None) -> bool:
    """
    Checks if a user is an administrator via their Telegram ID or configured username.
    """
    if user_id in settings.admin_ids:
        return True
    if username and settings.ADMIN_USERNAME:
        clean_cfg = settings.ADMIN_USERNAME.lstrip("@").strip().lower()
        clean_user = username.lstrip("@").strip().lower()
        if clean_cfg and clean_user == clean_cfg:
            return True
    return False


def get_main_keyboard(is_admin_user: bool = False) -> ReplyKeyboardMarkup:
    """
    Generates the main reply keyboard. If the user is an admin, an Admin Panel button is included.
    """
    rows = [
        [KeyboardButton(text="🔑 Моя лицензия"), KeyboardButton(text="💳 Купить лицензию")],
        [KeyboardButton(text="💻 Мои устройства"), KeyboardButton(text="🔄 Перенести лицензию")],
        [KeyboardButton(text="⚡ Как активировать"), KeyboardButton(text="💬 Поддержка")]
    ]
    if is_admin_user:
        rows.append([KeyboardButton(text="🛡️ Админ-панель")])
    return ReplyKeyboardMarkup(keyboard=rows, resize_keyboard=True)


def get_admin_inline_keyboard() -> InlineKeyboardMarkup:
    """
    Generates interactive inline keyboard for instant 1-click admin actions.
    """
    return InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(text="⚡ Ключ на 30 дней", callback_data="admin_gen_30"),
            InlineKeyboardButton(text="⚡ Ключ на 1 год", callback_data="admin_gen_365")
        ],
        [
            InlineKeyboardButton(text="♾️ Lifetime (навсегда)", callback_data="admin_gen_life"),
            InlineKeyboardButton(text="📊 Статистика", callback_data="admin_stats")
        ],
        [
            InlineKeyboardButton(text="👥 Список пользователей", callback_data="admin_users"),
            InlineKeyboardButton(text="📜 Логи безопасности", callback_data="admin_logs")
        ],
        [
            InlineKeyboardButton(text="🔄 Обновить панель", callback_data="admin_panel_refresh")
        ]
    ])


async def build_admin_panel_text(admin_name: str, admin_id: int, db) -> str:
    """
    Renders comprehensive admin panel overview with real-time stats and full command reference.
    """
    now = utc_now()
    total_users = (await db.execute(select(func.count(User.id)))).scalar() or 0
    total_licenses = (await db.execute(select(func.count(License.id)))).scalar() or 0
    active_licenses = (await db.execute(
        select(func.count(License.id)).where(License.status == "active", License.expires_at > now)
    )).scalar() or 0
    active_devices = (await db.execute(
        select(func.count(Device.id)).where(Device.is_active == True)
    )).scalar() or 0

    return (
        "🛡️ <b>ПАНЕЛЬ АДМИНИСТРАТОРА WinCleaner</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"👑 <b>Администратор:</b> {escape_html(admin_name)} (<code>{admin_id}</code>)\n"
        f"📊 <b>Сводка:</b> 👥 Пользователей: <b>{total_users}</b> | 🔑 Ключей: <b>{active_licenses}/{total_licenses}</b> активных | 💻 ПК: <b>{active_devices}</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "📋 <b>ДОСТУПНЫЕ КОМАНДЫ И ИНСТРУКЦИИ:</b>\n\n"
        "🔑 <b>Управление лицензиями:</b>\n"
        "• <code>/genkey &lt;дни&gt; [tg_id] [устройств]</code>\n"
        "  └ <i>Выпустить новый лицензионный ключ.</i>\n"
        "  Примеры:\n"
        "  ├ <code>/genkey 30</code> — на 30 дней (1 ПК, свободный)\n"
        "  ├ <code>/genkey 365 123456789 2</code> — на 1 год для TG ID на 2 ПК\n"
        "  └ <code>/genkey 36500 0 5</code> — бессрочный (Lifetime) на 5 ПК\n\n"
        "• <code>/info &lt;ключ&gt;</code>\n"
        "  └ <i>Досье ключа: статус, срок, привязанные ПК, HWID и кнопки управления.</i>\n"
        "  Пример: <code>/info WINC-XXXX-XXXX-XXXX</code>\n\n"
        "• <code>/extend &lt;ключ&gt; &lt;дни&gt;</code>\n"
        "  └ <i>Продлить срок действия ключа на N дней.</i>\n"
        "  Пример: <code>/extend WINC-XXXX-XXXX-XXXX 30</code>\n\n"
        "• <code>/revoke &lt;ключ&gt;</code>\n"
        "  └ <i>Отозвать ключ и моментально отключить все привязанные ПК.</i>\n"
        "  Пример: <code>/revoke WINC-XXXX-XXXX-XXXX</code>\n\n"
        "💻 <b>Управление устройствами и привязками:</b>\n"
        "• <code>/unbind &lt;ключ&gt;</code>\n"
        "  └ <i>Сбросить привязку всех устройств (для переноса на новый ПК).</i>\n"
        "  Пример: <code>/unbind WINC-XXXX-XXXX-XXXX</code>\n\n"
        "• <code>/devices &lt;ключ&gt;</code>\n"
        "  └ <i>Список всех устройств (HWID, имя ПК, дата активности) ключа.</i>\n"
        "  Пример: <code>/devices WINC-XXXX-XXXX-XXXX</code>\n\n"
        "📈 <b>База данных, аудит и рассылка:</b>\n"
        "• <code>/stats</code> — Детальная статистика по ключам, устройствам и пользователям\n"
        "• <code>/logs</code> — Последние 10 записей журнала безопасности и активаций\n"
        "• <code>/users</code> — Список последних зарегистрированных пользователей бота\n"
        "• <code>/broadcast &lt;текст&gt;</code> — Массовая рассылка уведомления всем пользователям\n"
        "• <code>/myid</code> — Узнать свой цифровой Telegram ID и статус\n"
        "• <code>/admin</code> — Открыть эту админ-панель\n\n"
        "⚡ <i>Также вы можете использовать кнопки быстрого действия ниже:</i>"
    )


async def send_admin_panel(message: Message):
    admin_name = message.from_user.full_name or message.from_user.username or "Admin"
    admin_id = message.from_user.id
    async with AsyncSessionLocal() as db:
        text = await build_admin_panel_text(admin_name, admin_id, db)
    await message.answer(text, reply_markup=get_admin_inline_keyboard(), parse_mode="HTML")


# ===========================================================================
# User Handlers
# ===========================================================================

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

    admin_flag = is_admin(tg_id, username)
    safe_name = escape_md(first_name) or "пользователь"
    welcome_text = (
        f"👋 Привет, {safe_name}!\n\n"
        "Добро пожаловать в центр управления лицензиями **WinCleaner**.\n\n"
        "Используйте кнопки меню ниже для управления вашими ключами и устройствами."
    )
    if admin_flag:
        welcome_text += "\n\n👑 *Вы авторизованы как администратор.* Вам доступна кнопка «🛡️ Админ-панель» и команда `/admin`."

    await message.answer(welcome_text, reply_markup=get_main_keyboard(admin_flag), parse_mode="Markdown")


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
        now = utc_now()
        for lic in licenses:
            dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
            dev_res = await db.execute(dev_stmt)
            devs = dev_res.scalars().all()

            status_icon = "🟢" if lic.status == "active" and lic.expires_at > now else "🔴"
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


@router.message(Command("myid"))
@router.message(Command("id"))
async def cmd_myid(message: Message):
    user_id = message.from_user.id
    username = message.from_user.username
    admin_status = "👑 <b>Администратор</b>" if is_admin(user_id, username) else "👤 <b>Пользователь</b>"
    uname_str = f"@{escape_html(username)}" if username else "<i>не установлен</i>"

    text = (
        f"🆔 <b>Ваш Telegram профиль:</b>\n\n"
        f"• Telegram ID: <code>{user_id}</code>\n"
        f"• Username: {uname_str}\n"
        f"• Роль в боте: {admin_status}\n\n"
        f"<i>При необходимости сообщите ваш Telegram ID администратору.</i>"
    )
    await message.answer(text, parse_mode="HTML")


@router.message(Command("help"))
async def cmd_help(message: Message):
    user_id = message.from_user.id
    username = message.from_user.username
    if is_admin(user_id, username):
        await send_admin_panel(message)
    else:
        text = (
            "ℹ️ <b>Центр помощи WinCleaner</b>\n\n"
            "Доступные функции:\n"
            "• <b>🔑 Моя лицензия</b> — просмотр ваших активных ключей и срока действия\n"
            "• <b>💳 Купить лицензию</b> — тарифы и оформление покупки\n"
            "• <b>💻 Мои устройства</b> — список ваших привязанных ПК\n"
            "• <b>🔄 Перенести лицензию</b> — сброс привязки для переноса на новый ПК\n"
            "• <b>⚡ Как активировать</b> — пошаговая инструкция по активации\n"
            "• <b>💬 Поддержка</b> — связь с администратором\n"
            "• <code>/myid</code> — узнать свой цифровой Telegram ID"
        )
        await message.answer(text, reply_markup=get_main_keyboard(False), parse_mode="HTML")


# ===========================================================================
# Admin Panel & Admin Command Handlers
# ===========================================================================

@router.message(Command("admin"))
@router.message(Command("panel"))
@router.message(Command("adminpanel"))
@router.message(F.text.in_({"🛡️ Админ-панель", "🛡 Админ-панель", "Админ-панель", "Админка"}))
async def handle_admin_panel_trigger(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return
    await send_admin_panel(message)


# ---------------------------------------------------------------------------
# Admin Inline Callbacks
# ---------------------------------------------------------------------------

@router.callback_query(F.data == "admin_panel_refresh")
async def cb_admin_panel_refresh(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return
    admin_name = callback.from_user.full_name or callback.from_user.username or "Admin"
    async with AsyncSessionLocal() as db:
        text = await build_admin_panel_text(admin_name, callback.from_user.id, db)
    try:
        await callback.message.edit_text(text, reply_markup=get_admin_inline_keyboard(), parse_mode="HTML")
        await callback.answer("🔄 Данные панели обновлены!")
    except Exception:
        await callback.answer("Данные уже актуальны.")


@router.callback_query(F.data.in_({"admin_gen_30", "admin_gen_365", "admin_gen_life"}))
async def cb_admin_quick_gen(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    if callback.data == "admin_gen_30":
        days = 30
        max_devices = 1
        label = "30 дней (1 ПК)"
    elif callback.data == "admin_gen_365":
        days = 365
        max_devices = 1
        label = "1 год (1 ПК)"
    else:
        days = 36500
        max_devices = 2
        label = "Lifetime / Бессрочно (2 ПК)"

    key = generate_key_string()
    now = utc_now()
    exp = now + timedelta(days=days)

    async with AsyncSessionLocal() as db:
        lic = License(
            license_key=key,
            user_id=None,
            status="active",
            max_devices=max_devices,
            created_at=now,
            expires_at=exp,
            notes=f"Quick issued by admin {callback.from_user.id} ({label})"
        )
        db.add(lic)
        await db.commit()

    await callback.answer("✅ Ключ создан!")
    exp_str = "Бессрочно (100 лет)" if days > 10000 else exp.strftime("%d.%m.%Y")
    reply = (
        f"✅ <b>Лицензионный ключ успешно создан ({label}):</b>\n\n"
        f"🔑 <code>{key}</code>\n\n"
        f"📅 Срок действия: <b>{exp_str}</b>\n"
        f"💻 Макс. устройств: <b>{max_devices}</b>\n"
        f"👤 Привязка к TG ID: <i>Любой пользователь</i>\n\n"
        f"<i>(Нажмите на ключ, чтобы скопировать в буфер обмена)</i>"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔍 Проверить этот ключ", callback_data=f"admin_quickinfo:{key}")],
        [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
    ])
    await callback.message.answer(reply, reply_markup=kb, parse_mode="HTML")


@router.callback_query(F.data == "admin_stats")
async def cb_admin_stats(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    now = utc_now()
    async with AsyncSessionLocal() as db:
        total_users = (await db.execute(select(func.count(User.id)))).scalar() or 0
        total_licenses = (await db.execute(select(func.count(License.id)))).scalar() or 0
        active_licenses = (await db.execute(
            select(func.count(License.id)).where(License.status == "active", License.expires_at > now)
        )).scalar() or 0
        revoked_licenses = (await db.execute(
            select(func.count(License.id)).where(License.status == "revoked")
        )).scalar() or 0
        active_devices = (await db.execute(
            select(func.count(Device.id)).where(Device.is_active == True)
        )).scalar() or 0
        total_devices = (await db.execute(select(func.count(Device.id)))).scalar() or 0
        total_logs = (await db.execute(select(func.count(ActivationLog.id)))).scalar() or 0

    stats_text = (
        "📊 <b>Детальная статистика WinCleaner:</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"👥 <b>Пользователи бота:</b> <code>{total_users}</code>\n"
        f"🔑 <b>Лицензии всего:</b> <code>{total_licenses}</code>\n"
        f"   • 🟢 Активных: <code>{active_licenses}</code>\n"
        f"   • 🚫 Отозванных: <code>{revoked_licenses}</code>\n"
        f"💻 <b>Устройства (ПК):</b>\n"
        f"   • 🟢 Активных: <code>{active_devices}</code>\n"
        f"   • 📦 Всего в базе: <code>{total_devices}</code>\n"
        f"📜 <b>Записей аудита / логов:</b> <code>{total_logs}</code>"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔄 Обновить", callback_data="admin_stats")],
        [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
    ])
    try:
        await callback.message.edit_text(stats_text, reply_markup=kb, parse_mode="HTML")
        await callback.answer()
    except Exception:
        await callback.answer("Данные актуальны!")


@router.callback_query(F.data == "admin_logs")
async def cb_admin_logs(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    async with AsyncSessionLocal() as db:
        stmt = select(ActivationLog).order_by(desc(ActivationLog.id)).limit(10)
        res = await db.execute(stmt)
        logs = res.scalars().all()

    if not logs:
        logs_text = "📜 <b>Журнал аудита и безопасности:</b>\n\nЗаписей пока нет."
    else:
        lines = ["📜 <b>Последние 10 событий аудита:</b>\n"]
        for log in logs:
            icon = "✅" if log.success else "⚠️"
            time_str = log.created_at.strftime("%d.%m %H:%M")
            safe_ip = escape_html(log.ip_address or "unknown")
            safe_det = escape_html(log.details or log.action)
            hwid_cut = log.hwid[:8] + "..." if len(log.hwid) > 8 else log.hwid
            lines.append(f"{icon} <code>{time_str}</code> | <b>{log.action}</b>\n   IP: <code>{safe_ip}</code> | HWID: <code>{hwid_cut}</code>\n   <i>{safe_det}</i>")
        logs_text = "\n".join(lines)

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔄 Обновить логи", callback_data="admin_logs")],
        [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
    ])
    try:
        await callback.message.edit_text(logs_text, reply_markup=kb, parse_mode="HTML")
        await callback.answer()
    except Exception:
        await callback.answer("Данные актуальны!")


@router.callback_query(F.data == "admin_users")
async def cb_admin_users(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    async with AsyncSessionLocal() as db:
        stmt = select(User).order_by(desc(User.id)).limit(15)
        res = await db.execute(stmt)
        users = res.scalars().all()

    if not users:
        users_text = "👥 <b>Пользователи бота:</b>\n\nВ базе данных пока нет зарегистрированных пользователей."
    else:
        lines = ["👥 <b>Последние 15 пользователей бота:</b>\n"]
        for u in users:
            reg_date = u.created_at.strftime("%d.%m.%Y") if u.created_at else "—"
            uname = f"@{escape_html(u.username)}" if u.username else "нет username"
            fname = escape_html(u.first_name or "Без имени")
            lines.append(f"• <b>{fname}</b> ({uname})\n  ID: <code>{u.telegram_id}</code> | Рег: {reg_date}")
        users_text = "\n".join(lines)

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔄 Обновить список", callback_data="admin_users")],
        [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
    ])
    try:
        await callback.message.edit_text(users_text, reply_markup=kb, parse_mode="HTML")
        await callback.answer()
    except Exception:
        await callback.answer("Данные актуальны!")


@router.callback_query(F.data.startswith("admin_quickinfo:"))
async def cb_admin_quickinfo(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    key = callback.data.split(":", 1)[1].strip()
    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await callback.answer("❌ Ключ не найден в базе данных.", show_alert=True)
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

        status_icon = "🟢" if lic.status == "active" and lic.expires_at > utc_now() else "🔴"
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

        kb = InlineKeyboardMarkup(inline_keyboard=[
            [
                InlineKeyboardButton(text="🔄 Сбросить устройства (Unbind)", callback_data=f"admin_act_unbind:{lic.license_key}"),
                InlineKeyboardButton(text="🚫 Отозвать ключ", callback_data=f"admin_act_revoke:{lic.license_key}")
            ],
            [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
        ])

        try:
            await callback.message.edit_text(info_text, reply_markup=kb, parse_mode="HTML")
            await callback.answer()
        except Exception:
            await callback.message.answer(info_text, reply_markup=kb, parse_mode="HTML")
            await callback.answer()


@router.callback_query(F.data.startswith("admin_act_unbind:"))
async def cb_admin_act_unbind(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    key = callback.data.split(":", 1)[1].strip()
    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await callback.answer("❌ Ключ не найден.", show_alert=True)
            return

        dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
        dev_res = await db.execute(dev_stmt)
        devs = dev_res.scalars().all()

        unbound_count = len(devs)
        for d in devs:
            d.is_active = False

        log = ActivationLog(
            license_id=lic.id,
            hwid="ADMIN_RESET_BUTTON",
            action="admin_unbind",
            ip_address="telegram_admin_ui",
            success=True,
            details=f"Admin {callback.from_user.id} unbound {unbound_count} device(s) via inline button"
        )
        db.add(log)
        await db.commit()

    await callback.answer(f"✅ Успешно отвязано {unbound_count} устройств!", show_alert=True)
    # Refresh the key info view
    callback.data = f"admin_quickinfo:{key}"
    await cb_admin_quickinfo(callback)


@router.callback_query(F.data.startswith("admin_act_revoke:"))
async def cb_admin_act_revoke(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    key = callback.data.split(":", 1)[1].strip()
    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await callback.answer("❌ Ключ не найден.", show_alert=True)
            return

        lic.status = "revoked"
        dev_stmt = select(Device).where(Device.license_id == lic.id)
        dev_res = await db.execute(dev_stmt)
        for d in dev_res.scalars().all():
            d.is_active = False

        log = ActivationLog(
            license_id=lic.id,
            hwid="ADMIN_REVOKE_BUTTON",
            action="admin_revoke",
            ip_address="telegram_admin_ui",
            success=True,
            details=f"Admin {callback.from_user.id} revoked key via inline button"
        )
        db.add(log)
        await db.commit()

    await callback.answer(f"🚫 Ключ {key} отозван!", show_alert=True)
    # Refresh the key info view
    callback.data = f"admin_quickinfo:{key}"
    await cb_admin_quickinfo(callback)


# ---------------------------------------------------------------------------
# Admin Text Commands
# ---------------------------------------------------------------------------

@router.message(Command("genkey"))
async def cmd_genkey(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
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
    now = utc_now()
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

    exp_str = "Бессрочно (100 лет)" if days > 10000 else exp.strftime("%d.%m.%Y")
    reply = (
        f"✅ <b>Лицензионный ключ успешно создан:</b>\n\n"
        f"🔑 <code>{key}</code>\n\n"
        f"📅 Срок: <b>{days} дней</b> (до {exp_str})\n"
        f"💻 Макс. устройств: <b>{max_devices}</b>\n"
        f"👤 Привязан к TG ID: <code>{target_tg_id or 'Любой пользователь'}</code>\n\n"
        f"<i>(Нажмите на ключ, чтобы скопировать в буфер)</i>"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔍 Проверить ключ", callback_data=f"admin_quickinfo:{key}")],
        [InlineKeyboardButton(text="🛡️ В админ-панель", callback_data="admin_panel_refresh")]
    ])
    await message.answer(reply, reply_markup=kb, parse_mode="HTML")


@router.message(Command("info"))
async def cmd_info(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    parts = message.text.split()
    if len(parts) < 2:
        await message.answer("Использование: <code>/info &lt;KEY&gt;</code>\nПример: <code>/info WINC-XXXX-XXXX-XXXX</code>", parse_mode="HTML")
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

        status_icon = "🟢" if lic.status == "active" and lic.expires_at > utc_now() else "🔴"
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

        kb = InlineKeyboardMarkup(inline_keyboard=[
            [
                InlineKeyboardButton(text="🔄 Сбросить устройства (Unbind)", callback_data=f"admin_act_unbind:{lic.license_key}"),
                InlineKeyboardButton(text="🚫 Отозвать ключ", callback_data=f"admin_act_revoke:{lic.license_key}")
            ],
            [InlineKeyboardButton(text="🛡️ В админ-панель", callback_data="admin_panel_refresh")]
        ])
        await message.answer(info_text, reply_markup=kb, parse_mode="HTML")


@router.message(Command("unbind"))
async def cmd_unbind(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    parts = message.text.split()
    if len(parts) < 2:
        await message.answer("Использование: <code>/unbind &lt;KEY&gt;</code>\nПример: <code>/unbind WINC-XXXX-XXXX-XXXX</code>", parse_mode="HTML")
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

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔍 Проверить ключ", callback_data=f"admin_quickinfo:{key}")],
        [InlineKeyboardButton(text="🛡️ В админ-панель", callback_data="admin_panel_refresh")]
    ])
    await message.answer(
        f"✅ Успешно отвязано устройств: <b>{unbound_count}</b> для ключа <code>{key}</code>.",
        reply_markup=kb,
        parse_mode="HTML"
    )


@router.message(Command("devices"))
async def cmd_devices(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
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

        kb = InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="🔄 Сбросить привязку (Unbind)", callback_data=f"admin_act_unbind:{lic.license_key}")],
            [InlineKeyboardButton(text="🛡️ В админ-панель", callback_data="admin_panel_refresh")]
        ])
        await message.answer("\n".join(lines), reply_markup=kb, parse_mode="HTML")


@router.message(Command("extend"))
async def cmd_extend(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    parts = message.text.split()
    if len(parts) < 3 or not parts[2].isdigit():
        await message.answer("Использование: <code>/extend &lt;KEY&gt; &lt;days&gt;</code>\nПример: <code>/extend WINC-XXXX-XXXX-XXXX 30</code>", parse_mode="HTML")
        return

    key = parts[1].strip().upper()
    days = int(parts[2])

    now = utc_now()
    async with AsyncSessionLocal() as db:
        stmt = select(License).where(License.license_key == key)
        res = await db.execute(stmt)
        lic = res.scalar_one_or_none()

        if not lic:
            await message.answer("❌ Ключ не найден.")
            return

        base_time = lic.expires_at if lic.expires_at > now else now
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

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔍 Проверить ключ", callback_data=f"admin_quickinfo:{key}")],
        [InlineKeyboardButton(text="🛡️ В админ-панель", callback_data="admin_panel_refresh")]
    ])
    await message.answer(
        f"✅ Лицензия <code>{key}</code> продлена на <b>{days} дней</b>.\n"
        f"Новый срок: <b>{lic.expires_at.strftime('%d.%m.%Y %H:%M')} UTC</b>",
        reply_markup=kb,
        parse_mode="HTML"
    )


@router.message(Command("revoke"))
async def cmd_revoke(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
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

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔍 Проверить ключ", callback_data=f"admin_quickinfo:{key}")],
        [InlineKeyboardButton(text="🛡️ В админ-панель", callback_data="admin_panel_refresh")]
    ])
    await message.answer(
        f"🚫 Ключ <code>{key}</code> отозван. Все привязанные устройства заблокированы.",
        reply_markup=kb,
        parse_mode="HTML"
    )


@router.message(Command("logs"))
async def cmd_logs(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
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

        kb = InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="🔄 Обновить логи", callback_data="admin_logs")],
            [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
        ])
        await message.answer("\n".join(lines), reply_markup=kb, parse_mode="HTML")


@router.message(Command("stats"))
async def cmd_stats(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    now = utc_now()
    async with AsyncSessionLocal() as db:
        total_users = (await db.execute(select(func.count(User.id)))).scalar() or 0
        total_licenses = (await db.execute(select(func.count(License.id)))).scalar() or 0
        active_licenses = (await db.execute(
            select(func.count(License.id)).where(License.status == "active", License.expires_at > now)
        )).scalar() or 0
        active_devices = (await db.execute(
            select(func.count(Device.id)).where(Device.is_active == True)
        )).scalar() or 0

    stats_text = (
        "📊 <b>Статистика системы лицензирования:</b>\n\n"
        f"👥 Всего пользователей: <code>{total_users}</code>\n"
        f"🔑 Всего лицензий: <code>{total_licenses}</code>\n"
        f"🟢 Активных лицензий: <code>{active_licenses}</code>\n"
        f"💻 Активных устройств: <code>{active_devices}</code>"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔄 Обновить", callback_data="admin_stats")],
        [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
    ])
    await message.answer(stats_text, reply_markup=kb, parse_mode="HTML")


@router.message(Command("users"))
async def cmd_users(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    async with AsyncSessionLocal() as db:
        stmt = select(User).order_by(desc(User.id)).limit(15)
        res = await db.execute(stmt)
        users = res.scalars().all()

        if not users:
            await message.answer("👥 В базе данных пока нет пользователей.", parse_mode="HTML")
            return

        lines = ["👥 <b>Последние пользователи бота:</b>\n"]
        for u in users:
            reg_date = u.created_at.strftime("%d.%m.%Y") if u.created_at else "—"
            uname = f"@{escape_html(u.username)}" if u.username else "нет username"
            fname = escape_html(u.first_name or "Без имени")
            lines.append(f"• <b>{fname}</b> ({uname})\n  ID: <code>{u.telegram_id}</code> | Рег: {reg_date}")

        kb = InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="🔄 Обновить список", callback_data="admin_users")],
            [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
        ])
        await message.answer("\n".join(lines), reply_markup=kb, parse_mode="HTML")


@router.message(Command("broadcast"))
async def cmd_broadcast(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    text_to_send = message.text.partition(" ")[2].strip()
    if not text_to_send:
        await message.answer(
            "Использование: <code>/broadcast &lt;текст сообщения&gt;</code>\n"
            "Пример: <code>/broadcast Внимание! Вышло обновление WinCleaner v2.0!</code>",
            parse_mode="HTML"
        )
        return

    bot = get_bot_instance()
    if not bot:
        await message.answer("❌ Экземпляр бота не инициализирован.")
        return

    async with AsyncSessionLocal() as db:
        stmt = select(User.telegram_id).distinct()
        res = await db.execute(stmt)
        user_ids = res.scalars().all()

    status_msg = await message.answer(f"📢 Запуск рассылки для {len(user_ids)} получателей...")
    success = 0
    failed = 0

    for uid in user_ids:
        try:
            await bot.send_message(uid, text_to_send, parse_mode="HTML")
            success += 1
            await asyncio.sleep(0.05)
        except Exception:
            failed += 1

    await status_msg.edit_text(
        f"📢 <b>Рассылка завершена:</b>\n\n"
        f"✅ Успешно доставлено: <b>{success}</b>\n"
        f"❌ Не доставлено (бот заблокирован или ошибка): <b>{failed}</b>\n"
        f"👥 Всего адресатов: <b>{len(user_ids)}</b>",
        parse_mode="HTML"
    )


@router.message()
async def handle_unknown_message(message: Message):
    admin_flag = is_admin(message.from_user.id, message.from_user.username)
    text = (
        "❓ Не удалось распознать команду.\n\n"
        "Пожалуйста, используйте кнопки меню ниже для управления лицензиями WinCleaner:"
    )
    await message.answer(text, reply_markup=get_main_keyboard(admin_flag))
