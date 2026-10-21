import os
import asyncio
import logging
from datetime import datetime, timedelta, timezone
from typing import Optional, List, Dict, Tuple

from aiogram import Bot, Dispatcher, Router, F
from aiogram.types import (
    Message,
    CallbackQuery,
    ReplyKeyboardMarkup,
    KeyboardButton,
    InlineKeyboardMarkup,
    InlineKeyboardButton,
    FSInputFile,
)
from aiogram.filters import CommandStart, Command
from sqlalchemy import select, func, desc, update

from app.config import settings
from app.database import AsyncSessionLocal
from app.models import User, License, Device, ActivationLog, SupportTicket, TicketMessage
from app.crypto import generate_key_string

logger = logging.getLogger("telegram_bot")
router = Router()

_bot_instance: Optional[Bot] = None
_dp_instance: Optional[Dispatcher] = None

# In-memory mapping: admin_chat_message_id -> (target_user_id, ticket_id)
# This enables direct Telegram "Reply" by admin to forwarded user messages
_admin_reply_map: Dict[int, Tuple[int, int]] = {}

# In-memory state: admin_id -> target_user_id for pending interactive text reply
_admin_waiting_reply: Dict[int, int] = {}

# Flood protection: user_id -> list of timestamps
_user_flood_tracker: Dict[int, List[float]] = {}
FLOOD_WINDOW = 30  # seconds
FLOOD_MAX_MSGS = 6  # max messages per window


def set_bot_instance(b: Bot):
    global _bot_instance
    _bot_instance = b


def get_bot_instance() -> Optional[Bot]:
    global _bot_instance
    return _bot_instance


def set_dp_instance(dp: Dispatcher):
    global _dp_instance
    _dp_instance = dp


def get_dp_instance() -> Optional[Dispatcher]:
    global _dp_instance
    return _dp_instance


def utc_now() -> datetime:
    """Returns timezone-naive UTC datetime for consistent database storage."""
    return datetime.now(timezone.utc).replace(tzinfo=None)


def escape_html(text: Optional[str]) -> str:
    if not text:
        return ""
    return str(text).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def is_admin(user_id: int, username: Optional[str] = None) -> bool:
    """Checks if a user is an administrator via their Telegram ID."""
    if user_id in settings.admin_ids:
        return True
    if username and settings.ADMIN_USERNAME:
        clean_cfg = settings.ADMIN_USERNAME.lstrip("@").strip().lower()
        clean_user = username.lstrip("@").strip().lower()
        if clean_cfg and clean_user == clean_cfg:
            return True
    return False


async def check_user_banned(user_id: int) -> bool:
    try:
        async with AsyncSessionLocal() as db:
            stmt = select(User.is_banned).where(User.telegram_id == user_id)
            res = await db.execute(stmt)
            banned = res.scalar_one_or_none()
            return bool(banned)
    except Exception as e:
        logger.warning(f"check_user_banned fallback: {e}")
        return False


def check_flood(user_id: int) -> bool:
    import time
    now = time.time()
    stamps = _user_flood_tracker.get(user_id, [])
    stamps = [t for t in stamps if now - t < FLOOD_WINDOW]
    if len(stamps) >= FLOOD_MAX_MSGS:
        return True
    stamps.append(now)
    _user_flood_tracker[user_id] = stamps
    return False


async def send_banner_or_text(
    message: Message,
    banner_path: str,
    caption: str,
    reply_markup=None,
    parse_mode: str = "HTML"
) -> Message:
    """
    Sends a high-resolution banner photo if available on disk,
    otherwise falls back gracefully to a clean text message.
    """
    if banner_path and os.path.isfile(banner_path):
        try:
            photo = FSInputFile(banner_path)
            return await message.answer_photo(
                photo=photo,
                caption=caption,
                reply_markup=reply_markup,
                parse_mode=parse_mode
            )
        except Exception as e:
            logger.warning(f"Failed to send banner photo ({banner_path}): {e}. Falling back to text.")

    return await message.answer(
        text=caption,
        reply_markup=reply_markup,
        parse_mode=parse_mode
    )


async def send_admin_alert(
    html_text: str,
    reply_markup: Optional[InlineKeyboardMarkup] = None,
    photo_file_id: Optional[str] = None,
    doc_file_id: Optional[str] = None,
    ticket_context: Optional[Tuple[int, int]] = None  # (target_user_id, ticket_id)
):
    """
    Sends security and support alerts to all configured administrator Telegram IDs.
    Registers admin message IDs to allow native Telegram 'Reply' back to the client.
    """
    bot = get_bot_instance()
    if not bot:
        return

    for admin_id in settings.admin_ids:
        try:
            sent_msg: Optional[Message] = None
            if photo_file_id:
                sent_msg = await bot.send_photo(
                    admin_id,
                    photo=photo_file_id,
                    caption=html_text,
                    reply_markup=reply_markup,
                    parse_mode="HTML"
                )
            elif doc_file_id:
                sent_msg = await bot.send_document(
                    admin_id,
                    document=doc_file_id,
                    caption=html_text,
                    reply_markup=reply_markup,
                    parse_mode="HTML"
                )
            else:
                sent_msg = await bot.send_message(
                    admin_id,
                    html_text,
                    reply_markup=reply_markup,
                    parse_mode="HTML"
                )

            if sent_msg and ticket_context:
                _admin_reply_map[sent_msg.message_id] = ticket_context

        except Exception as e:
            logger.warning(f"Failed to send admin alert to {admin_id}: {e}")


# ===========================================================================
# Keyboards
# ===========================================================================

def get_main_keyboard(is_admin_user: bool = False) -> ReplyKeyboardMarkup:
    """Generates the main reply menu buttons."""
    rows = [
        [KeyboardButton(text="🔑 Моя лицензия"), KeyboardButton(text="💳 Купить лицензию")],
        [KeyboardButton(text="💻 Мои устройства"), KeyboardButton(text="🔄 Перенести лицензию")],
        [KeyboardButton(text="⚡ Как активировать"), KeyboardButton(text="💬 Поддержка")]
    ]
    if is_admin_user:
        rows.append([KeyboardButton(text="🛡️ Админ-панель")])
    return ReplyKeyboardMarkup(keyboard=rows, resize_keyboard=True)


def get_tariffs_inline_keyboard() -> InlineKeyboardMarkup:
    """Tariff selection keyboard with pricing and direct booking."""
    return InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(text="🌟 1 месяц — 299 ₽", callback_data="buy_tariff:month:299"),
            InlineKeyboardButton(text="🚀 1 год — 990 ₽ (Хит)", callback_data="buy_tariff:year:990")
        ],
        [
            InlineKeyboardButton(text="👑 Lifetime (Навсегда) — 1 990 ₽", callback_data="buy_tariff:lifetime:1990")
        ],
        [
            InlineKeyboardButton(text="💬 Задать вопрос по оплате / Другие способы", callback_data="user_open_support_buy")
        ]
    ])


def get_support_inline_keyboard() -> InlineKeyboardMarkup:
    """Support options keyboard."""
    return InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(text="✍️ Написать оператору поддержки", callback_data="user_start_ticket"),
            InlineKeyboardButton(text="❓ Частые вопросы (FAQ)", callback_data="user_faq")
        ],
        [
            InlineKeyboardButton(text="💳 Оформить покупку", callback_data="nav_tariffs")
        ]
    ])


def get_admin_inline_keyboard() -> InlineKeyboardMarkup:
    """Generates interactive inline keyboard for instant 1-click admin actions."""
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
            InlineKeyboardButton(text="👥 Пользователи", callback_data="admin_users"),
            InlineKeyboardButton(text="📜 Логи безопасности", callback_data="admin_logs")
        ],
        [
            InlineKeyboardButton(text="🔄 Обновить панель", callback_data="admin_panel_refresh")
        ]
    ])


# ===========================================================================
# Admin Panel Builders
# ===========================================================================

async def build_admin_panel_text(admin_name: str, admin_id: int, db) -> str:
    """Renders comprehensive admin panel overview with real-time stats and full command reference."""
    now = utc_now()
    total_users = (await db.execute(select(func.count(User.id)))).scalar() or 0
    total_licenses = (await db.execute(select(func.count(License.id)))).scalar() or 0
    active_licenses = (await db.execute(
        select(func.count(License.id)).where(License.status == "active", License.expires_at > now)
    )).scalar() or 0
    active_devices = (await db.execute(
        select(func.count(Device.id)).where(Device.is_active == True)
    )).scalar() or 0
    open_tickets = (await db.execute(
        select(func.count(SupportTicket.id)).where(SupportTicket.status == "open")
    )).scalar() or 0

    return (
        "🛡️ <b>ПАНЕЛЬ АДМИНИСТРАТОРА WinCleaner</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"👑 <b>Администратор:</b> {escape_html(admin_name)} (<code>{admin_id}</code>)\n"
        f"📊 <b>Сводка:</b> 👥 Клиентов: <b>{total_users}</b> | 🔑 Ключей: <b>{active_licenses}/{total_licenses}</b> | 💻 ПК: <b>{active_devices}</b>\n"
        f"📩 <b>Открытых обращений в саппорт:</b> <b>{open_tickets}</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "📋 <b>УПРАВЛЕНИЕ И КОМАНДЫ:</b>\n\n"
        "💬 <b>Анонимная поддержка и связь:</b>\n"
        "• <i>Просто ответьте (Reply) на любое сообщение тикета</i> — бот сам передаст ваш ответ клиенту!\n"
        "• <code>/reply &lt;tg_id&gt; &lt;текст&gt;</code> — Написать клиенту от лица бота\n"
        "• <code>/sendkey &lt;tg_id&gt; &lt;дни&gt; [ПК] [заметка]</code> — Выдать ключ и сразу доставить в ЛС\n"
        "• <code>/user &lt;tg_id&gt;</code> — Полное досье клиента (ключи, HWID, логи)\n"
        "• <code>/ban &lt;tg_id&gt;</code> и <code>/unban &lt;tg_id&gt;</code> — Блокировка спамеров\n\n"
        "🔑 <b>Управление лицензиями:</b>\n"
        "• <code>/genkey &lt;дни&gt; [tg_id] [устройств]</code> — Выпустить ключ без отправки\n"
        "• <code>/info &lt;ключ&gt;</code> — Досье ключа, статус и привязанные ПК\n"
        "• <code>/extend &lt;ключ&gt; &lt;дни&gt;</code> — Продлить ключ на N дней\n"
        "• <code>/unbind &lt;ключ&gt;</code> — Сбросить привязку всех ПК ключа\n"
        "• <code>/revoke &lt;ключ&gt;</code> — Отозвать лицензию навсегда\n"
        "• <code>/devices &lt;ключ&gt;</code> — Список ПК ключа с HWID\n\n"
        "📈 <b>Аудит и рассылка:</b>\n"
        "• <code>/stats</code> — Детальная статистика базы\n"
        "• <code>/logs</code> — Журнал последних активаций и событий\n"
        "• <code>/users</code> — Список последних пользователей\n"
        "• <code>/broadcast &lt;текст&gt;</code> — Рассылка всем клиентам\n\n"
        "⚡ <i>Быстрые действия доступны на кнопках ниже:</i>"
    )


async def send_admin_panel(message: Message):
    admin_name = message.from_user.full_name or message.from_user.username or "Admin"
    admin_id = message.from_user.id
    async with AsyncSessionLocal() as db:
        text = await build_admin_panel_text(admin_name, admin_id, db)
    await message.answer(text, reply_markup=get_admin_inline_keyboard(), parse_mode="HTML")


# ===========================================================================
# User Handlers (Completely Anonymous, No Admin Mentions)
# ===========================================================================

@router.message(CommandStart())
async def cmd_start(message: Message):
    tg_id = message.from_user.id
    username = message.from_user.username
    first_name = message.from_user.first_name

    if await check_user_banned(tg_id):
        await message.answer("⛔ Доступ к сервису заблокирован.")
        return

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
    safe_name = escape_html(first_name) or "пользователь"

    welcome_text = (
        f"👋 <b>Добро пожаловать в WinCleaner, {safe_name}!</b>\n\n"
        "🛡️ <b>WinCleaner</b> — профессиональный комплекс для глубокой очистки, "
        "оптимизации реестра, защиты приватности и ускорения Windows.\n\n"
        "📌 <b>Возможности центра управления:</b>\n"
        "• 🔑 <b>Моя лицензия</b> — просмотр ваших ключей и срока действия\n"
        "• 💳 <b>Купить лицензию</b> — выбор и заказ тарифа (1 мес, 1 год, Lifetime)\n"
        "• 💻 <b>Мои устройства</b> — список ваших привязанных ПК\n"
        "• 🔄 <b>Перенести лицензию</b> — моментальный сброс HWID для нового ПК\n"
        "• ⚡ <b>Как активировать</b> — пошаговый гид по запуску приложения\n"
        "• 💬 <b>Поддержка</b> — анонимный диалог со специалистом прямо здесь!\n\n"
        "Используйте кнопки меню ниже для навигации ⬇️"
    )

    if admin_flag:
        welcome_text += "\n\n👑 <b>Режим администратора активен.</b> Доступна «🛡️ Админ-панель» и <code>/admin</code>."

    await send_banner_or_text(
        message,
        settings.banner_main,
        welcome_text,
        reply_markup=get_main_keyboard(admin_flag)
    )


@router.message(F.text == "🔑 Моя лицензия")
async def handle_my_license(message: Message):
    tg_id = message.from_user.id
    if await check_user_banned(tg_id):
        return

    async with AsyncSessionLocal() as db:
        stmt = select(User).where(User.telegram_id == tg_id)
        res = await db.execute(stmt)
        user = res.scalar_one_or_none()

        if not user:
            await message.answer("Пожалуйста, нажмите /start для регистрации.")
            return

        lic_stmt = select(License).where(License.user_id == user.id).order_by(License.id.desc())
        lic_res = await db.execute(lic_stmt)
        licenses = lic_res.scalars().all()

        if not licenses:
            text = (
                "❌ <b>У вас пока нет активных лицензий WinCleaner.</b>\n\n"
                "Приобретите ключ, чтобы разблокировать полную очистку и максимальную скорость вашей системы!"
            )
            kb = InlineKeyboardMarkup(inline_keyboard=[
                [InlineKeyboardButton(text="💳 Выбрать тариф и купить", callback_data="nav_tariffs")],
                [InlineKeyboardButton(text="💬 Написать в поддержку", callback_data="user_start_ticket")]
            ])
            await message.answer(text, reply_markup=kb, parse_mode="HTML")
            return

        now = utc_now()
        for lic in licenses:
            dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
            dev_res = await db.execute(dev_stmt)
            devs = dev_res.scalars().all()

            is_active = (lic.status == "active" and lic.expires_at > now)
            status_icon = "🟢" if is_active else "🔴"
            status_str = "Активна" if is_active else ("Отозвана" if lic.status == "revoked" else "Истекла")
            exp_str = "Бессрочно (Lifetime)" if (lic.expires_at - now).days > 10000 else lic.expires_at.strftime("%d.%m.%Y")

            dev_info = ""
            if devs:
                dev_info = "\n   💻 <i>Привязанные ПК:</i> " + ", ".join(escape_html(d.device_name) for d in devs)

            card = (
                f"{status_icon} <b>Лицензия WinCleaner:</b>\n\n"
                f"🔑 Ключ: <code>{lic.license_key}</code>\n"
                f"📅 Статус: <b>{status_str}</b>\n"
                f"⏳ Срок действия: <b>{exp_str}</b>\n"
                f"💻 Устройств: <b>{len(devs)}/{lic.max_devices}</b>"
                f"{dev_info}\n\n"
                f"<i>(Нажмите на ключ выше, чтобы скопировать его в буфер)</i>"
            )

            kb = InlineKeyboardMarkup(inline_keyboard=[
                [InlineKeyboardButton(text="🔄 Отвязать ПК (сбросить HWID)", callback_data=f"user_unbind_key:{lic.license_key}")],
                [InlineKeyboardButton(text="⚡ Инструкция по активации", callback_data="nav_how_activate")]
            ])
            await message.answer(card, reply_markup=kb, parse_mode="HTML")


@router.message(F.text == "💳 Купить лицензию")
async def handle_buy_license(message: Message):
    if await check_user_banned(message.from_user.id):
        return

    text = (
        "💳 <b>Официальные тарифные планы WinCleaner:</b>\n\n"
        "• 🌟 <b>1 месяц — 299 ₽</b> (1 ПК)\n"
        "  └ Быстрый старт, полная очистка реестра, кэша и следов телеметрии.\n\n"
        "• 🚀 <b>1 год — 990 ₽</b> (1 ПК) — 🔥 <b>ХИТ ПРОДАЖ! (Экономия 72%)</b>\n"
        "  └ Целый год безупречной и быстрой работы вашей Windows + приоритет.\n\n"
        "• 👑 <b>Lifetime — 1 990 ₽</b> (2 ПК) — ♾️ <b>Навсегда!</b>\n"
        "  └ Бессрочный доступ для 2 ваших компьютеров, пожизненные обновления.\n\n"
        "✨ <b>В каждый тариф входит:</b>\n"
        "✅ Удаление всего системного мусора и следов шпионажа\n"
        "✅ Оптимизация сети, пинга и оперативной памяти\n"
        "✅ Свободный перенос лицензии при переустановке Windows\n"
        "✅ Полная техническая поддержка 24/7 прямо в этом боте\n\n"
        "Выберите желаемый тариф ниже ⬇️"
    )

    await send_banner_or_text(
        message,
        settings.banner_tariffs,
        text,
        reply_markup=get_tariffs_inline_keyboard()
    )


@router.callback_query(F.data == "nav_tariffs")
async def cb_nav_tariffs(callback: CallbackQuery):
    await callback.answer()
    await handle_buy_license(callback.message)


@router.callback_query(F.data.startswith("buy_tariff:"))
async def cb_buy_tariff(callback: CallbackQuery):
    """
    User selected a tariff to buy.
    Registers a purchase inquiry, notifies admins with user details,
    and shows payment info to user with direct support in bot.
    """
    parts = callback.data.split(":")
    tariff_code = parts[1]
    price = parts[2]

    tariff_titles = {
        "month": "1 месяц (1 ПК)",
        "year": "1 год (1 ПК) — Хит продаж",
        "lifetime": "Lifetime Навсегда (2 ПК)"
    }
    tariff_name = tariff_titles.get(tariff_code, tariff_code)

    user_id = callback.from_user.id
    user_name = callback.from_user.full_name or "Пользователь"
    user_uname = f"@{callback.from_user.username}" if callback.from_user.username else "[нет username]"

    # Record or update support ticket for purchase
    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == user_id))
        user = u_res.scalar_one_or_none()
        if not user:
            user = User(telegram_id=user_id, username=callback.from_user.username, first_name=callback.from_user.first_name)
            db.add(user)
            await db.flush()

        ticket = SupportTicket(
            user_id=user.id,
            user_telegram_id=user_id,
            status="open",
            ticket_type="purchase",
            subject=f"Заказ: {tariff_name} ({price} ₽)"
        )
        db.add(ticket)
        await db.flush()

        msg_record = TicketMessage(
            ticket_id=ticket.id,
            sender_type="user",
            sender_telegram_id=user_id,
            text=f"Выбран тариф: {tariff_name} за {price} ₽"
        )
        db.add(msg_record)
        await db.commit()
        ticket_id = ticket.id

    # Alert administrators with user details and instant key issue button
    days_map = {"month": 30, "year": 365, "lifetime": 36500}
    devs_map = {"month": 1, "year": 1, "lifetime": 2}
    issued_days = days_map.get(tariff_code, 30)
    issued_devs = devs_map.get(tariff_code, 1)

    admin_alert_text = (
        "🛍️ <b>НОВАЯ ЗАЯВКА НА ПОКУПКУ ЛИЦЕНЗИИ!</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"👤 <b>Клиент:</b> {escape_html(user_name)} ({user_uname})\n"
        f"🆔 <b>Telegram ID:</b> <code>{user_id}</code>\n"
        f"📦 <b>Тариф:</b> <b>{tariff_name}</b>\n"
        f"💰 <b>Сумма к оплате:</b> <b>{price} ₽</b>\n"
        f"📅 <b>Время заявки:</b> {utc_now().strftime('%d.%m.%Y %H:%M')} UTC\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "<i>Вы можете нажать кнопку ниже для мгновенной выдачи ключа клиенту или ответить текстом:</i>"
    )

    admin_kb = InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(
                text=f"⚡ Выдать ключ ({price} ₽)",
                callback_data=f"admin_quick_issue:{user_id}:{issued_days}:{issued_devs}"
            ),
            InlineKeyboardButton(
                text="✍️ Ответить клиенту",
                callback_data=f"admin_prompt_reply:{user_id}"
            )
        ],
        [
            InlineKeyboardButton(text="👤 Досье клиента", callback_data=f"admin_dossier:{user_id}"),
            InlineKeyboardButton(text="🔒 Закрыть заявку", callback_data=f"admin_close_ticket:{ticket_id}")
        ]
    ])

    await send_admin_alert(admin_alert_text, reply_markup=admin_kb, ticket_context=(user_id, ticket_id))

    # User confirmation message
    user_reply = (
        f"🛍️ <b>Вы выбрали тариф: {tariff_name} — {price} ₽</b>\n\n"
        "💳 <b>Доступные способы оплаты:</b>\n"
        "• <b>СБП (Система быстрых платежей) / Перевод на карту</b>\n"
        "• <b>Криптовалюта (USDT TRC-20, TON, BTC)</b>\n"
        "• <b>ЮMoney / Любые российские и зарубежные карты</b>\n\n"
        "💬 <b>Как завершить оплату?</b>\n"
        "Напишите прямо в этот чат удобный для вас банк или способ оплаты (например: <i>«СБП Тинькофф»</i> или <i>«USDT»</i>), "
        "либо просто отправьте скриншот чека об оплате.\n\n"
        "Оператор поддержки ответит вам прямо в этом диалоге и сразу выдаст лицензионный ключ!"
    )

    user_kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="✍️ Написать оператору реквизиты", callback_data="user_start_ticket")],
        [InlineKeyboardButton(text="🔙 Назад к тарифам", callback_data="nav_tariffs")]
    ])

    await callback.message.answer(user_reply, reply_markup=user_kb, parse_mode="HTML")
    await callback.answer("Заявка зарегистрирована!")


@router.message(F.text == "⚡ Как активировать")
async def handle_how_to_activate(message: Message):
    if await check_user_banned(message.from_user.id):
        return

    text = (
        "⚡ <b>Инструкция по активации WinCleaner:</b>\n\n"
        "1️⃣ <b>Скачайте и запустите</b> файл <code>WinCleaner.exe</code> от имени администратора.\n"
        "2️⃣ В появившемся окне авторизации вставьте ваш ключ вида:\n"
        "   <code>WINC-XXXX-XXXX-XXXX</code>\n"
        "3️⃣ Нажмите кнопку <b>«⚡ Активировать»</b>.\n"
        "4️⃣ Программа свяжется с защищенным сервером, автоматически привяжет ваш ПК и разблокирует доступ ко всем возможностям!\n\n"
        "💡 <b>Полезные советы:</b>\n"
        "• Ключ можно переносить между компьютерами через кнопку <b>«🔄 Перенести лицензию»</b>.\n"
        "• Если возникла любая ошибка — просто напишите в <b>«💬 Поддержка»</b>, и оператор поможет за пару минут."
    )

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔑 Моя лицензия", callback_data="nav_my_license")],
        [InlineKeyboardButton(text="💬 Задать вопрос поддержке", callback_data="user_start_ticket")]
    ])

    await send_banner_or_text(
        message,
        settings.banner_activate,
        text,
        reply_markup=kb
    )


@router.callback_query(F.data == "nav_how_activate")
async def cb_nav_how_activate(callback: CallbackQuery):
    await callback.answer()
    await handle_how_to_activate(callback.message)


@router.callback_query(F.data == "nav_my_license")
async def cb_nav_my_license(callback: CallbackQuery):
    await callback.answer()
    await handle_my_license(callback.message)


@router.message(F.text == "💻 Мои устройства")
async def handle_my_devices(message: Message):
    tg_id = message.from_user.id
    if await check_user_banned(tg_id):
        return

    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == tg_id))
        user = u_res.scalar_one_or_none()

        if not user:
            await message.answer("Пожалуйста, нажмите /start.")
            return

        stmt = select(Device).join(License).where(License.user_id == user.id, Device.is_active == True)
        res = await db.execute(stmt)
        devices = res.scalars().all()

        if not devices:
            text = (
                "💻 <b>Привязанные устройства:</b>\n\n"
                "На ваших лицензиях пока нет активных ПК.\n"
                "При первом запуске и активации ключа ваш компьютер появится здесь автоматически."
            )
            await message.answer(text, parse_mode="HTML")
            return

        text_lines = ["💻 <b>Ваши активные устройства:</b>\n"]
        for d in devices:
            safe_dev = escape_html(d.device_name)
            last_date = d.last_seen.strftime('%d.%m.%Y %H:%M')
            text_lines.append(
                f"• 🖥️ <b>{safe_dev}</b>\n"
                f"  HWID: <code>{d.hwid[:16]}...</code>\n"
                f"  Активность: <code>{last_date} UTC</code>\n"
            )

        text_lines.append("\n<i>Вы можете отвязать устройства в любой момент кнопкой ниже:</i>")
        kb = InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="🔄 Сбросить привязку всех ПК", callback_data="user_confirm_unbind_all")]
        ])
        await message.answer("\n".join(text_lines), reply_markup=kb, parse_mode="HTML")


@router.message(F.text == "🔄 Перенести лицензию")
async def handle_transfer_license(message: Message):
    tg_id = message.from_user.id
    if await check_user_banned(tg_id):
        return

    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == tg_id))
        user = u_res.scalar_one_or_none()

        if not user:
            await message.answer("Пожалуйста, нажмите /start.")
            return

        lic_stmt = select(License).where(License.user_id == user.id, License.status == "active")
        lic_res = await db.execute(lic_stmt)
        licenses = lic_res.scalars().all()

        if not licenses:
            await message.answer("❌ У вас нет активных лицензий для переноса.")
            return

        # Check total active devices
        dev_count = 0
        for lic in licenses:
            dev_stmt = select(func.count(Device.id)).where(Device.license_id == lic.id, Device.is_active == True)
            dev_count += (await db.execute(dev_stmt)).scalar() or 0

        text = (
            "🔄 <b>Перенос лицензии на новый компьютер:</b>\n\n"
            f"Сейчас на ваших лицензиях привязано устройств: <b>{dev_count}</b>.\n\n"
            "Если вы переустановили Windows, сменили материнскую плату или купили новый ПК, "
            "нажмите кнопку подтверждения ниже. Привязка старых ПК будет моментально снята, "
            "и вы сможете активировать ваш ключ на новом оборудовании!"
        )

        kb = InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="✅ Да, сбросить привязку устройств", callback_data="user_do_unbind_all")],
            [InlineKeyboardButton(text="❌ Отмена", callback_data="user_cancel_action")]
        ])
        await message.answer(text, reply_markup=kb, parse_mode="HTML")


@router.callback_query(F.data == "user_confirm_unbind_all")
async def cb_user_confirm_unbind_all(callback: CallbackQuery):
    await callback.answer()
    await handle_transfer_license(callback.message)


@router.callback_query(F.data == "user_do_unbind_all")
async def cb_user_do_unbind_all(callback: CallbackQuery):
    tg_id = callback.from_user.id
    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == tg_id))
        user = u_res.scalar_one_or_none()
        if not user:
            await callback.answer("Ошибка: пользователь не найден.")
            return

        lic_stmt = select(License).where(License.user_id == user.id, License.status == "active")
        lic_res = await db.execute(lic_stmt)
        licenses = lic_res.scalars().all()

        unbound_count = 0
        for lic in licenses:
            dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
            dev_res = await db.execute(dev_stmt)
            for d in dev_res.scalars().all():
                d.is_active = False
                unbound_count += 1

        if unbound_count > 0:
            log = ActivationLog(
                license_id=None,
                hwid="USER_SELF_RESET",
                action="transfer",
                ip_address="telegram_bot",
                success=True,
                details=f"User {tg_id} unbound {unbound_count} devices"
            )
            db.add(log)
            await db.commit()

    reply = (
        f"✅ <b>Успешно отвязано устройств: {unbound_count}.</b>\n\n"
        "Теперь вы можете открыть WinCleaner на новом компьютере и ввести ваш ключ — "
        "он активируется без проблем!"
    )
    await callback.message.edit_text(reply, parse_mode="HTML")
    await callback.answer("Устройства успешно отвязаны!")


@router.callback_query(F.data.startswith("user_unbind_key:"))
async def cb_user_unbind_single_key(callback: CallbackQuery):
    key = callback.data.split(":", 1)[1].strip()
    tg_id = callback.from_user.id

    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == tg_id))
        user = u_res.scalar_one_or_none()
        if not user:
            await callback.answer("Пользователь не найден.")
            return

        lic_stmt = select(License).where(License.license_key == key, License.user_id == user.id)
        lic_res = await db.execute(lic_stmt)
        lic = lic_res.scalar_one_or_none()

        if not lic:
            await callback.answer("Лицензия не принадлежит вашему аккаунту.", show_alert=True)
            return

        dev_stmt = select(Device).where(Device.license_id == lic.id, Device.is_active == True)
        dev_res = await db.execute(dev_stmt)
        devs = dev_res.scalars().all()

        unbound_count = len(devs)
        for d in devs:
            d.is_active = False

        if unbound_count > 0:
            log = ActivationLog(
                license_id=lic.id,
                hwid="USER_KEY_RESET",
                action="transfer",
                ip_address="telegram_bot",
                success=True,
                details=f"User {tg_id} unbound {unbound_count} devices for key {key}"
            )
            db.add(log)
            await db.commit()

    await callback.answer(f"✅ Отвязано устройств: {unbound_count}!", show_alert=True)
    await callback.message.answer(
        f"✅ <b>Устройства для ключа <code>{key}</code> успешно отвязаны!</b>\n"
        "Вы можете активировать этот ключ на новом ПК прямо сейчас.",
        parse_mode="HTML"
    )


@router.callback_query(F.data == "user_cancel_action")
async def cb_user_cancel_action(callback: CallbackQuery):
    await callback.message.edit_text("Действие отменено.")
    await callback.answer()


@router.message(F.text == "💬 Поддержка")
async def handle_support(message: Message):
    if await check_user_banned(message.from_user.id):
        return

    text = (
        "💬 <b>Служба поддержки WinCleaner</b>\n\n"
        "Возникли технические вопросы, ошибки активации или проблемы после чистки?\n\n"
        "✍️ <b>Просто напишите ваш вопрос прямо в этот чат!</b>\n"
        "Вы также можете отправить нам скриншот ошибки или текстовый лог.\n\n"
        "🔒 <b>Полная конфиденциальность:</b>\n"
        "• Все сообщения передаются операторам поддержки через зашифрованный шлюз.\n"
        "• Ответ специалиста придет вам прямо сюда от лица бота.\n"
        "• Среднее время ответа оператора: 5–15 минут."
    )

    await send_banner_or_text(
        message,
        settings.banner_support,
        text,
        reply_markup=get_support_inline_keyboard()
    )


@router.callback_query(F.data.in_({"user_start_ticket", "user_open_support_buy"}))
async def cb_user_start_ticket(callback: CallbackQuery):
    await callback.answer()
    prompt = (
        "✍️ <b>Напишите ваше сообщение прямо в этот чат.</b>\n\n"
        "Опишите ваш вопрос, желаемый способ оплаты или прикрепите скриншот. "
        "Оператор поддержки сразу получит обращение и ответит вам здесь же!"
    )
    await callback.message.answer(prompt, parse_mode="HTML")


@router.callback_query(F.data == "user_faq")
async def cb_user_faq(callback: CallbackQuery):
    faq_text = (
        "❓ <b>Часто задаваемые вопросы (FAQ):</b>\n\n"
        "🔹 <b>Блокирует ли антивирус WinCleaner?</b>\n"
        "WinCleaner использует глубокие системные функции очистки реестра и сетевых служб, "
        "поэтому некоторые параноидальные антивирусы могут выдать ложное срабатывание (False Positive). "
        "Добавьте программу в исключения или запускайте от имени администратора.\n\n"
        "🔹 <b>Что если я переустановил Windows?</b>\n"
        "Просто нажмите <b>«🔄 Перенести лицензию»</b> в этом боте, привязка сбросится, "
        "и вы сможете использовать тот же ключ снова!\n\n"
        "🔹 <b>Нужен ли постоянный интернет?</b>\n"
        "Интернет нужен только один раз во время активации и валидации ключа. Далее приложение работает офлайн.\n\n"
        "🔹 <b>Как оплатить?</b>\n"
        "Мы принимаем СБП, карты РФ и мира, а также USDT/TON. Нажмите <b>«💳 Купить лицензию»</b>."
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="💬 Задать свой вопрос", callback_data="user_start_ticket")],
        [InlineKeyboardButton(text="🔙 В меню поддержки", callback_data="nav_support")]
    ])
    try:
        await callback.message.edit_text(faq_text, reply_markup=kb, parse_mode="HTML")
    except Exception:
        await callback.message.answer(faq_text, reply_markup=kb, parse_mode="HTML")
    await callback.answer()


@router.callback_query(F.data == "nav_support")
async def cb_nav_support(callback: CallbackQuery):
    await callback.answer()
    await handle_support(callback.message)


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
        f"• Роль в боте: {admin_status}"
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
            "• <b>🔑 Моя лицензия</b> — ваши активные ключи и управление ПК\n"
            "• <b>💳 Купить лицензию</b> — тарифы и оплата\n"
            "• <b>💻 Мои устройства</b> — список ваших привязанных ПК\n"
            "• <b>🔄 Перенести лицензию</b> — сброс привязки для нового ПК\n"
            "• <b>⚡ Как активировать</b> — инструкция по запуску и вводу ключа\n"
            "• <b>💬 Поддержка</b> — прямая анонимная связь с поддержкой\n"
            "• <code>/myid</code> — ваш цифровой Telegram ID"
        )
        await message.answer(text, reply_markup=get_main_keyboard(False), parse_mode="HTML")


# ===========================================================================
# Admin Text Commands & Management
# ===========================================================================

@router.message(Command("admin"))
@router.message(Command("panel"))
@router.message(Command("adminpanel"))
@router.message(F.text.in_({"🛡️ Админ-панель", "🛡 Админ-панель", "Админ-панель", "Админка"}))
async def handle_admin_panel_trigger(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return
    await send_admin_panel(message)


@router.message(Command("reply"))
@router.message(Command("r"))
async def cmd_admin_reply(message: Message):
    """
    Allows admin to reply to any user by Telegram ID:
    /reply <tg_id> <reply text>
    """
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    parts = message.text.split(maxsplit=2)
    if len(parts) < 3 or not parts[1].isdigit():
        await message.answer(
            "Использование: <code>/reply &lt;tg_id&gt; &lt;текст ответа&gt;</code>\n"
            "Пример: <code>/reply 123456789 Здравствуйте! Ваш ключ продлен.</code>",
            parse_mode="HTML"
        )
        return

    target_id = int(parts[1])
    reply_text = parts[2].strip()

    bot = get_bot_instance()
    if not bot:
        await message.answer("❌ Бот не инициализирован.")
        return

    formatted_client_msg = (
        "💬 <b>Служба поддержки WinCleaner:</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"{escape_html(reply_text)}\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "<i>(Вы можете продолжить диалог, просто ответив в этот чат)</i>"
    )

    try:
        await bot.send_message(target_id, formatted_client_msg, parse_mode="HTML")
        await message.answer(f"✅ <b>Ответ успешно доставлен пользователю <code>{target_id}</code>!</b>", parse_mode="HTML")
    except Exception as e:
        await message.answer(f"❌ <b>Не удалось доставить сообщение пользователю <code>{target_id}</code>:</b>\n<code>{escape_html(str(e))}</code>", parse_mode="HTML")


@router.message(Command("sendkey"))
async def cmd_sendkey(message: Message):
    """
    Admin command to generate a key AND immediately deliver it to the user in Telegram PM:
    /sendkey <tg_id> <days> [max_devices] [notes]
    """
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    parts = message.text.split()
    if len(parts) < 3 or not parts[1].isdigit() or not parts[2].isdigit():
        await message.answer(
            "Использование: <code>/sendkey &lt;tg_id&gt; &lt;дни&gt; [устройств] [заметка]</code>\n\n"
            "Примеры:\n"
            "• <code>/sendkey 123456789 30</code> — 30 дней на 1 ПК\n"
            "• <code>/sendkey 123456789 365 1 Оплата СБП</code> — 1 год на 1 ПК\n"
            "• <code>/sendkey 123456789 36500 2 Lifetime</code> — Навсегда на 2 ПК",
            parse_mode="HTML"
        )
        return

    target_tg_id = int(parts[1])
    days = int(parts[2])
    max_devices = int(parts[3]) if len(parts) > 3 and parts[3].isdigit() else 1
    notes = " ".join(parts[4:]) if len(parts) > 4 else f"Issued by admin {message.from_user.id}"

    key = generate_key_string()
    now = utc_now()
    exp = now + timedelta(days=days)

    bot = get_bot_instance()
    if not bot:
        await message.answer("❌ Бот не инициализирован.")
        return

    async with AsyncSessionLocal() as db:
        u_stmt = select(User).where(User.telegram_id == target_tg_id)
        u_res = await db.execute(u_stmt)
        user = u_res.scalar_one_or_none()
        if not user:
            user = User(telegram_id=target_tg_id)
            db.add(user)
            await db.flush()

        lic = License(
            license_key=key,
            user_id=user.id,
            status="active",
            max_devices=max_devices,
            created_at=now,
            expires_at=exp,
            notes=notes
        )
        db.add(lic)
        await db.commit()

    exp_str = "Бессрочно (Lifetime)" if days > 10000 else exp.strftime("%d.%m.%Y")

    # Send congratulations card directly to user
    user_card = (
        "🎉 <b>Вам выдана лицензия WinCleaner!</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"🔑 Ваш ключ: <code>{key}</code>\n"
        f"⏳ Срок действия: <b>{exp_str}</b> ({days} дн.)\n"
        f"💻 Доступно компьютеров: <b>{max_devices} ПК</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n\n"
        "⚡ <b>Как активировать:</b>\n"
        "1. Запустите <code>WinCleaner.exe</code> от имени администратора\n"
        "2. Скопируйте ключ выше и вставьте в окно активации\n"
        "3. Нажмите «⚡ Активировать»!\n\n"
        "<i>(Нажмите на ключ выше, чтобы скопировать его в буфер обмена)</i>"
    )

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔑 Мои лицензии", callback_data="nav_my_license")]
    ])

    delivery_status = "✅ <b>Ключ успешно доставлен в ЛС клиенту!</b>"
    try:
        await bot.send_message(target_tg_id, user_card, reply_markup=kb, parse_mode="HTML")
    except Exception as e:
        delivery_status = f"⚠️ <b>Внимание:</b> Ключ создан в базе, но не доставлен в ЛС (бот заблокирован клиентом): <code>{escape_html(str(e))}</code>"

    admin_reply = (
        f"✅ <b>Лицензия успешно создана:</b>\n\n"
        f"🔑 <code>{key}</code>\n"
        f"👤 Клиент: <code>{target_tg_id}</code>\n"
        f"📅 Срок: <b>{days} дней</b> (до {exp_str})\n"
        f"💻 Устройств: <b>{max_devices}</b>\n\n"
        f"{delivery_status}"
    )

    await message.answer(admin_reply, parse_mode="HTML")


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

    exp_str = "Бессрочно (Lifetime)" if days > 10000 else exp.strftime("%d.%m.%Y")
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


@router.message(Command("user"))
async def cmd_user_dossier(message: Message):
    """
    Shows complete dossier of a user:
    /user <tg_id or @username>
    """
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    parts = message.text.split()
    if len(parts) < 2:
        await message.answer("Использование: <code>/user &lt;tg_id или @username&gt;</code>", parse_mode="HTML")
        return

    arg = parts[1].strip()

    async with AsyncSessionLocal() as db:
        if arg.isdigit():
            stmt = select(User).where(User.telegram_id == int(arg))
        else:
            clean_un = arg.lstrip("@").lower()
            stmt = select(User).where(func.lower(User.username) == clean_un)

        res = await db.execute(stmt)
        user = res.scalar_one_or_none()

        if not user:
            await message.answer("❌ Пользователь не найден в базе данных.")
            return

        # Fetch licenses
        lic_stmt = select(License).where(License.user_id == user.id).order_by(desc(License.id))
        lic_res = await db.execute(lic_stmt)
        licenses = lic_res.scalars().all()

        # Fetch devices
        dev_stmt = select(Device).join(License).where(License.user_id == user.id)
        dev_res = await db.execute(dev_stmt)
        devices = dev_res.scalars().all()

        uname_str = f"@{escape_html(user.username)}" if user.username else "нет username"
        fname_str = escape_html(user.first_name or "Без имени")
        reg_date = user.created_at.strftime("%d.%m.%Y %H:%M") if user.created_at else "—"
        ban_status = "🔴 <b>ЗАБЛОКИРОВАН</b>" if user.is_banned else "🟢 Активен"

        lic_lines = []
        now = utc_now()
        for l in licenses:
            icon = "🟢" if l.status == "active" and l.expires_at > now else "🔴"
            exp_date = l.expires_at.strftime("%d.%m.%Y")
            lic_lines.append(f"  {icon} <code>{l.license_key}</code> ({l.status}, до {exp_date}, max {l.max_devices} ПК)")

        dev_lines = []
        for d in devices:
            d_icon = "🟢" if d.is_active else "⚪"
            dev_lines.append(f"  {d_icon} <b>{escape_html(d.device_name)}</b> | HWID: <code>{d.hwid[:12]}...</code>")

        dossier_text = (
            f"👤 <b>ДОСЬЕ ПОЛЬЗОВАТЕЛЯ:</b>\n"
            f"━━━━━━━━━━━━━━━━━━━━━━\n"
            f"• <b>Имя:</b> {fname_str} ({uname_str})\n"
            f"• <b>Telegram ID:</b> <code>{user.telegram_id}</code>\n"
            f"• <b>Статус:</b> {ban_status}\n"
            f"• <b>Дата регистрации:</b> {reg_date}\n\n"
            f"🔑 <b>Лицензии ({len(licenses)}):</b>\n" +
            ("\n".join(lic_lines) if lic_lines else "  Нет лицензий") + "\n\n"
            f"💻 <b>Компьютеры ({len(devices)}):</b>\n" +
            ("\n".join(dev_lines) if dev_lines else "  Нет устройств")
        )

        ban_btn_text = "🟢 Разблокировать" if user.is_banned else "🚫 Заблокировать"
        ban_action = "admin_unban" if user.is_banned else "admin_ban"

        kb = InlineKeyboardMarkup(inline_keyboard=[
            [
                InlineKeyboardButton(text="⚡ Выдать ключ", callback_data=f"admin_quick_issue:{user.telegram_id}:365:1"),
                InlineKeyboardButton(text="✍️ Написать", callback_data=f"admin_prompt_reply:{user.telegram_id}")
            ],
            [
                InlineKeyboardButton(text=ban_btn_text, callback_data=f"{ban_action}:{user.telegram_id}"),
                InlineKeyboardButton(text="🛡️ В админ-панель", callback_data="admin_panel_refresh")
            ]
        ])

        await message.answer(dossier_text, reply_markup=kb, parse_mode="HTML")


@router.message(Command("ban"))
async def cmd_ban_user(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return
    parts = message.text.split()
    if len(parts) < 2 or not parts[1].isdigit():
        await message.answer("Использование: <code>/ban &lt;tg_id&gt;</code>", parse_mode="HTML")
        return
    target_id = int(parts[1])
    async with AsyncSessionLocal() as db:
        await db.execute(update(User).where(User.telegram_id == target_id).values(is_banned=True))
        await db.commit()
    await message.answer(f"🚫 Пользователь <code>{target_id}</code> заблокирован в боте.", parse_mode="HTML")


@router.message(Command("unban"))
async def cmd_unban_user(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return
    parts = message.text.split()
    if len(parts) < 2 or not parts[1].isdigit():
        await message.answer("Использование: <code>/unban &lt;tg_id&gt;</code>", parse_mode="HTML")
        return
    target_id = int(parts[1])
    async with AsyncSessionLocal() as db:
        await db.execute(update(User).where(User.telegram_id == target_id).values(is_banned=False))
        await db.commit()
    await message.answer(f"🟢 Пользователь <code>{target_id}</code> разблокирован.", parse_mode="HTML")


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
            ban_mark = " [🚫]" if u.is_banned else ""
            lines.append(f"• <b>{fname}</b> ({uname}){ban_mark}\n  ID: <code>{u.telegram_id}</code> | Рег: {reg_date}")

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


# ===========================================================================
# Admin Callbacks
# ===========================================================================

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


@router.callback_query(F.data.startswith("admin_quick_issue:"))
async def cb_admin_quick_issue(callback: CallbackQuery):
    """
    1-Click Issue Key to User:
    admin_quick_issue:<user_id>:<days>:<max_devices>
    """
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    parts = callback.data.split(":")
    target_id = int(parts[1])
    days = int(parts[2])
    max_devices = int(parts[3])

    key = generate_key_string()
    now = utc_now()
    exp = now + timedelta(days=days)

    async with AsyncSessionLocal() as db:
        u_stmt = select(User).where(User.telegram_id == target_id)
        u_res = await db.execute(u_stmt)
        user = u_res.scalar_one_or_none()
        if not user:
            user = User(telegram_id=target_id)
            db.add(user)
            await db.flush()

        lic = License(
            license_key=key,
            user_id=user.id,
            status="active",
            max_devices=max_devices,
            created_at=now,
            expires_at=exp,
            notes=f"Instant issued by admin {callback.from_user.id}"
        )
        db.add(lic)
        await db.commit()

    bot = get_bot_instance()
    exp_str = "Бессрочно (Lifetime)" if days > 10000 else exp.strftime("%d.%m.%Y")

    user_card = (
        "🎉 <b>Вам выдана лицензия WinCleaner!</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"🔑 Ваш ключ: <code>{key}</code>\n"
        f"⏳ Срок действия: <b>{exp_str}</b> ({days} дн.)\n"
        f"💻 Компьютеров: <b>{max_devices} ПК</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n\n"
        "⚡ <b>Как активировать:</b>\n"
        "1. Запустите <code>WinCleaner.exe</code> от имени администратора\n"
        "2. Вставьте ваш ключ и нажмите «Активировать»!\n\n"
        "<i>(Нажмите на ключ выше, чтобы скопировать)</i>"
    )
    user_kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔑 Мои лицензии", callback_data="nav_my_license")]
    ])

    try:
        if bot:
            await bot.send_message(target_id, user_card, reply_markup=user_kb, parse_mode="HTML")
            delivery = "✅ Доставлено клиенту в ЛС"
    except Exception as e:
        delivery = f"⚠️ Ошибка доставки в ЛС: {escape_html(str(e))}"

    await callback.answer("Ключ выдан!")
    await callback.message.reply(
        f"✅ <b>Ключ <code>{key}</code> успешно создан для пользователя <code>{target_id}</code>!</b>\n{delivery}",
        parse_mode="HTML"
    )


@router.callback_query(F.data.startswith("admin_prompt_reply:"))
async def cb_admin_prompt_reply(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    target_id = int(callback.data.split(":")[1])
    _admin_waiting_reply[callback.from_user.id] = target_id

    await callback.answer()
    await callback.message.reply(
        f"✍️ <b>Введите текст ответа для клиента <code>{target_id}</code>:</b>\n\n"
        f"<i>(Следующее ваше текстовое сообщение или файл будет немедленно отправлен этому клиенту от лица бота)</i>",
        parse_mode="HTML"
    )


@router.callback_query(F.data.startswith("admin_dossier:"))
async def cb_admin_dossier(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    target_id = int(callback.data.split(":")[1])
    # Reuse command logic
    msg = callback.message
    msg.text = f"/user {target_id}"
    await cmd_user_dossier(msg)
    await callback.answer()


@router.callback_query(F.data.startswith("admin_ban:"))
async def cb_admin_ban_user(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    target_id = int(callback.data.split(":")[1])
    async with AsyncSessionLocal() as db:
        await db.execute(update(User).where(User.telegram_id == target_id).values(is_banned=True))
        await db.commit()

    await callback.answer(f"Пользователь {target_id} заблокирован!", show_alert=True)


@router.callback_query(F.data.startswith("admin_unban:"))
async def cb_admin_unban_user(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    target_id = int(callback.data.split(":")[1])
    async with AsyncSessionLocal() as db:
        await db.execute(update(User).where(User.telegram_id == target_id).values(is_banned=False))
        await db.commit()

    await callback.answer(f"Пользователь {target_id} разблокирован!", show_alert=True)


@router.callback_query(F.data.startswith("admin_close_ticket:"))
async def cb_admin_close_ticket(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    ticket_id = int(callback.data.split(":")[1])
    async with AsyncSessionLocal() as db:
        await db.execute(update(SupportTicket).where(SupportTicket.id == ticket_id).values(status="closed"))
        await db.commit()

    await callback.answer("Обращение закрыто.")
    try:
        await callback.message.edit_text(f"{callback.message.text}\n\n🔒 <i>[Обращение закрыто администратором]</i>")
    except Exception:
        pass


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
            ban_mark = " [🚫]" if u.is_banned else ""
            lines.append(f"• <b>{fname}</b> ({uname}){ban_mark}\n  ID: <code>{u.telegram_id}</code> | Рег: {reg_date}")
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
    callback.data = f"admin_quickinfo:{key}"
    await cb_admin_quickinfo(callback)


# ===========================================================================
# Central Message Router & Support Relay System
# (Handles user messages -> admin, and admin replies -> user)
# ===========================================================================

@router.message()
async def handle_incoming_relay_and_support(message: Message):
    sender_id = message.from_user.id
    admin_flag = is_admin(sender_id, message.from_user.username)

    # -----------------------------------------------------------------------
    # Case 1: An Administrator is replying
    # -----------------------------------------------------------------------
    if admin_flag:
        # Check if admin is replying to a forwarded ticket message via native Telegram Reply
        target_info = None
        if message.reply_to_message:
            rep_id = message.reply_to_message.message_id
            target_info = _admin_reply_map.get(rep_id)

        # Or check if admin was prompted via [✍️ Ответить] button
        if not target_info and sender_id in _admin_waiting_reply:
            target_uid = _admin_waiting_reply.pop(sender_id)
            target_info = (target_uid, 0)

        if target_info:
            target_user_id, ticket_id = target_info
            bot = get_bot_instance()
            if not bot:
                await message.reply("❌ Экземпляр бота не найден.")
                return

            # Format reply from the Bot (completely anonymous: no admin name or username)
            reply_header = (
                "💬 <b>Служба поддержки WinCleaner:</b>\n"
                "━━━━━━━━━━━━━━━━━━━━━━\n"
            )
            reply_footer = (
                "\n━━━━━━━━━━━━━━━━━━━━━━\n"
                "<i>(Вы можете продолжить диалог, ответив в этот чат)</i>"
            )

            try:
                if message.photo:
                    photo_id = message.photo[-1].file_id
                    cap = f"{reply_header}{escape_html(message.caption or '')}{reply_footer}"
                    await bot.send_photo(target_user_id, photo=photo_id, caption=cap, parse_mode="HTML")
                elif message.document:
                    doc_id = message.document.file_id
                    cap = f"{reply_header}{escape_html(message.caption or '')}{reply_footer}"
                    await bot.send_document(target_user_id, document=doc_id, caption=cap, parse_mode="HTML")
                elif message.text:
                    body = f"{reply_header}{escape_html(message.text)}{reply_footer}"
                    await bot.send_message(target_user_id, body, parse_mode="HTML")
                else:
                    await message.reply("⚠️ Формат сообщения не поддерживается для пересылки.")
                    return

                # Record in database if ticket_id is valid
                if ticket_id > 0:
                    async with AsyncSessionLocal() as db:
                        msg_rec = TicketMessage(
                            ticket_id=ticket_id,
                            sender_type="admin",
                            sender_telegram_id=sender_id,
                            text=message.text or message.caption or "[Media]",
                            has_media=bool(message.photo or message.document),
                            media_type="photo" if message.photo else ("doc" if message.document else None)
                        )
                        db.add(msg_rec)
                        await db.execute(update(SupportTicket).where(SupportTicket.id == ticket_id).values(status="answered"))
                        await db.commit()

                await message.reply(f"✅ <b>Ответ успешно доставлен клиенту <code>{target_user_id}</code>!</b>", parse_mode="HTML")
                return

            except Exception as e:
                await message.reply(f"❌ Ошибка отправки клиенту <code>{target_user_id}</code>: <code>{escape_html(str(e))}</code>", parse_mode="HTML")
                return

    # -----------------------------------------------------------------------
    # Case 2: A regular user sends any message (Question / Issue / Proof of payment)
    # -----------------------------------------------------------------------
    if await check_user_banned(sender_id):
        await message.answer("⛔ Доступ к сервису ограничен.")
        return

    if check_flood(sender_id):
        await message.answer("⏳ Пожалуйста, не отправляйте сообщения слишком часто. Подождите немного.")
        return

    user_name = message.from_user.full_name or "Пользователь"
    user_uname = f"@{message.from_user.username}" if message.from_user.username else "[нет username]"

    # Fetch user data and active license summary for rich admin dossier
    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == sender_id))
        user = u_res.scalar_one_or_none()
        if not user:
            user = User(telegram_id=sender_id, username=message.from_user.username, first_name=message.from_user.first_name)
            db.add(user)
            await db.flush()

        lic_stmt = select(License).where(License.user_id == user.id, License.status == "active", License.expires_at > utc_now())
        lic_res = await db.execute(lic_stmt)
        active_lics = lic_res.scalars().all()

        dev_stmt = select(func.count(Device.id)).join(License).where(License.user_id == user.id, Device.is_active == True)
        dev_count = (await db.execute(dev_stmt)).scalar() or 0

        # Find or create open ticket
        t_stmt = select(SupportTicket).where(SupportTicket.user_id == user.id, SupportTicket.status.in_(["open", "answered"])).order_by(desc(SupportTicket.id))
        t_res = await db.execute(t_stmt)
        ticket = t_res.scalar_one_or_none()

        if not ticket:
            ticket = SupportTicket(
                user_id=user.id,
                user_telegram_id=sender_id,
                status="open",
                ticket_type="support",
                subject="Обращение в чате"
            )
            db.add(ticket)
            await db.flush()
        else:
            ticket.status = "open"
            ticket.updated_at = utc_now()

        # Save message
        content_text = message.text or message.caption or "[Медиафайл]"
        msg_rec = TicketMessage(
            ticket_id=ticket.id,
            sender_type="user",
            sender_telegram_id=sender_id,
            text=content_text,
            has_media=bool(message.photo or message.document)
        )
        db.add(msg_rec)
        await db.commit()
        ticket_id = ticket.id

    # Format admin dossier
    if active_lics:
        lic_summary = f"🟢 {len(active_lics)} шт. (до {active_lics[0].expires_at.strftime('%d.%m.%Y')})"
    else:
        lic_summary = "⚪ Нет активных лицензий"

    admin_notification = (
        "📩 <b>НОВОЕ ОБРАЩЕНИЕ В ПОДДЕРЖКУ!</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"👤 <b>От:</b> {escape_html(user_name)} ({user_uname})\n"
        f"🆔 <b>Telegram ID:</b> <code>{sender_id}</code>\n"
        f"🔑 <b>Лицензия:</b> {lic_summary}\n"
        f"💻 <b>Устройств:</b> {dev_count} ПК\n"
        f"📅 <b>Время:</b> {utc_now().strftime('%d.%m.%Y %H:%M')} UTC\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"💬 <b>Сообщение:</b>\n"
        f"{escape_html(content_text)}\n\n"
        "<i>(Вы можете ответить на это сообщение в Telegram через Reply, чтобы переслать ответ клиенту)</i>"
    )

    admin_kb = InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(text="✍️ Ответить", callback_data=f"admin_prompt_reply:{sender_id}"),
            InlineKeyboardButton(text="⚡ Выдать ключ (30д)", callback_data=f"admin_quick_issue:{sender_id}:30:1")
        ],
        [
            InlineKeyboardButton(text="👤 Досье пользователя", callback_data=f"admin_dossier:{sender_id}"),
            InlineKeyboardButton(text="🔒 Закрыть тикет", callback_data=f"admin_close_ticket:{ticket_id}")
        ]
    ])

    photo_id = message.photo[-1].file_id if message.photo else None
    doc_id = message.document.file_id if message.document else None

    # Alert administrators with media if present
    await send_admin_alert(
        admin_notification,
        reply_markup=admin_kb,
        photo_file_id=photo_id,
        doc_file_id=doc_id,
        ticket_context=(sender_id, ticket_id)
    )

    # Reassure client with clean confirmation
    client_confirm = (
        "✅ <b>Ваше сообщение передано в службу поддержки WinCleaner!</b>\n\n"
        "Оператор ознакомится с ним и ответит вам прямо в этот диалог в ближайшее время."
    )
    await message.answer(client_confirm, parse_mode="HTML")
