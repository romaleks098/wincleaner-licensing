import os
import asyncio
import logging
import secrets
import time
from datetime import datetime, timedelta, timezone
from typing import Optional, List, Dict, Tuple, Set

from aiogram import Bot, Dispatcher, Router, F
from aiogram.types import (
    Message,
    CallbackQuery,
    ReplyKeyboardMarkup,
    KeyboardButton,
    InlineKeyboardMarkup,
    InlineKeyboardButton,
    FSInputFile,
    PreCheckoutQuery,
    LabeledPrice,
    SuccessfulPayment,
)
from aiogram.filters import CommandStart, Command
from sqlalchemy import select, func, desc, update

from app.config import settings
from app.database import AsyncSessionLocal
from app.models import (
    User, License, Device, ActivationLog, SupportTicket, TicketMessage,
    PromoCode, Order, ReferralHistory, FeatureIdea, FeatureVote
)
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

# In-memory state: user_id -> waiting to enter promo code
_user_waiting_promo: Set[int] = set()

# In-memory state: user_id -> waiting to propose a feature idea
_user_waiting_idea: Set[int] = set()

# In-memory state: user_id -> active promo details (e.g. {"code": "START20", "discount_percent": 20})
_user_active_promo: Dict[int, Dict] = {}

# In-memory state: admin_id -> waiting for broadcast announcement text
_admin_waiting_announce: Set[int] = set()

# Standard catalog pricing (RUB and Telegram Stars)
BASE_PRICES = {"month": 299, "year": 990, "lifetime": 1990, "hwid": 150}
STARS_PRICES = {"month": 150, "year": 500, "lifetime": 990, "hwid": 75}
REFERRAL_PERCENT = 15  # 15% cashback for referrers

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
    """Generates the main reply menu buttons with full business & self-service suite."""
    rows = [
        [KeyboardButton(text="🔑 Моя лицензия"), KeyboardButton(text="💳 Купить лицензию")],
        [KeyboardButton(text="💻 Мои устройства"), KeyboardButton(text="🔄 Перенести лицензию")],
        [KeyboardButton(text="🤝 Партнерка"), KeyboardButton(text="🎟 Промокод")],
        [KeyboardButton(text="💡 Голосование"), KeyboardButton(text="📢 Что нового")],
        [KeyboardButton(text="❓ FAQ & Помощь"), KeyboardButton(text="💬 Поддержка")]
    ]
    if is_admin_user:
        rows.append([KeyboardButton(text="🛡️ Админ-панель")])
    return ReplyKeyboardMarkup(keyboard=rows, resize_keyboard=True)


def get_tariffs_inline_keyboard(discount_percent: int = 0) -> InlineKeyboardMarkup:
    """Tariff selection keyboard with optional promo discount and 150 RUB HWID reset."""
    def apply_disc(base: int) -> int:
        if discount_percent > 0:
            return max(1, int(base * (100 - discount_percent) / 100))
        return base

    p_month = apply_disc(299)
    p_year = apply_disc(990)
    p_life = apply_disc(1990)
    p_hwid = 150  # fixed price for HWID unbind

    tag = f" (-{discount_percent}%)" if discount_percent > 0 else ""

    promo_btn_text = f"✅ Скидка {discount_percent}% активна!" if discount_percent > 0 else "🎟 Ввести промокод"

    return InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(text=f"🌟 1 месяц — {p_month} ₽{tag}", callback_data=f"buy_tariff:month:{p_month}"),
            InlineKeyboardButton(text=f"🚀 1 год — {p_year} ₽ (Хит){tag}", callback_data=f"buy_tariff:year:{p_year}")
        ],
        [
            InlineKeyboardButton(text=f"👑 Lifetime (Навсегда) — {p_life} ₽{tag}", callback_data=f"buy_tariff:lifetime:{p_life}")
        ],
        [
            InlineKeyboardButton(text=f"🔄 Сброс HWID / Перенос ПК — {p_hwid} ₽", callback_data=f"buy_tariff:hwid:{p_hwid}")
        ],
        [
            InlineKeyboardButton(text=promo_btn_text, callback_data="user_enter_promo"),
            InlineKeyboardButton(text="💬 Другие способы", callback_data="user_open_support_buy")
        ]
    ])


def get_payment_methods_keyboard(order_id: str, item_type: str, price_rub: int, stars_amount: int) -> InlineKeyboardMarkup:
    """Renders selectable payment methods for an order."""
    buttons = [
        [
            InlineKeyboardButton(
                text=f"⭐ Telegram Stars ({stars_amount} ⭐)",
                callback_data=f"pay_stars:{order_id}:{item_type}:{stars_amount}:{price_rub}"
            )
        ],
        [
            InlineKeyboardButton(
                text=f"💰 Оплатить с баланса ({price_rub} ₽)",
                callback_data=f"pay_balance:{order_id}:{item_type}:{price_rub}"
            )
        ],
        [
            InlineKeyboardButton(
                text=f"💳 СБП / Карты РФ ({price_rub} ₽)",
                callback_data=f"pay_sbp:{order_id}:{item_type}:{price_rub}"
            ),
            InlineKeyboardButton(
                text="💎 CryptoBot (USDT/TON)",
                callback_data=f"pay_crypto:{order_id}:{item_type}:{price_rub}"
            )
        ],
        [
            InlineKeyboardButton(text="🔙 Отмена", callback_data="nav_tariffs")
        ]
    ]
    return InlineKeyboardMarkup(inline_keyboard=buttons)


def get_faq_inline_keyboard() -> InlineKeyboardMarkup:
    """Interactive Knowledge Base / FAQ topics."""
    return InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🛡️ Антивирус / Windows Defender", callback_data="faq_item:antivirus")],
        [InlineKeyboardButton(text="⚡ Руководство по глубокой очистке", callback_data="faq_item:cleaning")],
        [InlineKeyboardButton(text="🔄 Смена ПК и сброс HWID (150 ₽)", callback_data="faq_item:hwid")],
        [InlineKeyboardButton(text="🔑 Вопросы по ключам и активации", callback_data="faq_item:keys")],
        [InlineKeyboardButton(text="⚙️ Ошибки и их быстрое решение", callback_data="faq_item:errors")],
        [InlineKeyboardButton(text="✍️ Задать вопрос поддержке 24/7", callback_data="user_start_ticket")]
    ])


def get_support_inline_keyboard() -> InlineKeyboardMarkup:
    """Support options keyboard."""
    return InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(text="✍️ Написать оператору поддержки", callback_data="user_start_ticket"),
            InlineKeyboardButton(text="❓ База знаний (FAQ)", callback_data="user_faq")
        ],
        [
            InlineKeyboardButton(text="💳 Оформить покупку", callback_data="nav_tariffs")
        ]
    ])


def get_admin_inline_keyboard() -> InlineKeyboardMarkup:
    """Generates interactive inline keyboard for instant 1-click admin actions."""
    return InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(text="⚡ Ключ 30д", callback_data="admin_gen_30"),
            InlineKeyboardButton(text="⚡ Ключ 1 год", callback_data="admin_gen_365"),
            InlineKeyboardButton(text="♾️ Lifetime", callback_data="admin_gen_life")
        ],
        [
            InlineKeyboardButton(text="📊 Бизнес-аналитика", callback_data="admin_analytics"),
            InlineKeyboardButton(text="📈 Базовая статистика", callback_data="admin_stats")
        ],
        [
            InlineKeyboardButton(text="🎟 Промокоды", callback_data="admin_promos_list"),
            InlineKeyboardButton(text="👥 Пользователи", callback_data="admin_users")
        ],
        [
            InlineKeyboardButton(text="💡 Идеи сообщества", callback_data="admin_ideas_list"),
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
    total_rev = (await db.execute(
        select(func.coalesce(func.sum(Order.amount_rub), 0)).where(Order.status == "paid")
    )).scalar() or 0
    active_promos = (await db.execute(
        select(func.count(PromoCode.id)).where(PromoCode.is_active == True)
    )).scalar() or 0
    total_ideas = (await db.execute(select(func.count(FeatureIdea.id)))).scalar() or 0

    return (
        "🛡️ <b>ПАНЕЛЬ АДМИНИСТРАТОРА WinCleaner</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"👑 <b>Администратор:</b> {escape_html(admin_name)} (<code>{admin_id}</code>)\n"
        f"💰 <b>Выручка:</b> <b>{total_rev:,} ₽</b> | 🎟 <b>Промокодов:</b> <b>{active_promos}</b> | 💡 <b>Идей:</b> <b>{total_ideas}</b>\n"
        f"📊 <b>Сводка:</b> 👥 Клиентов: <b>{total_users}</b> | 🔑 Ключей: <b>{active_licenses}/{total_licenses}</b> | 💻 ПК: <b>{active_devices}</b>\n"
        f"📩 <b>Открытых тикетов в саппорт:</b> <b>{open_tickets}</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "📋 <b>УПРАВЛЕНИЕ И КОМАНДЫ:</b>\n\n"
        "📊 <b>Финансы и бизнес:</b>\n"
        "• <code>/analytics</code> или <code>/finance</code> — Детальная выручка (24ч, 7д, 30д), CR, рефералы\n"
        "• <code>/createpromo &lt;КОД&gt; &lt;СКИДКА%&gt; [ЛИМИТ]</code> — Создать промокод со скидкой\n"
        "• <code>/promos</code> — Список всех промокодов системы\n\n"
        "💡 <b>Идеи сообщества и голосование:</b>\n"
        "• <code>/admin_ideas</code> — Список предложенных функций\n"
        "• <code>/set_idea_status &lt;id&gt; &lt;статус&gt;</code> — Изменить статус (approved, in_progress, completed, rejected)\n"
        "• <code>/delete_idea &lt;id&gt;</code> — Удалить идею\n\n"
        "📊 <b>Финансы и бизнес:</b>\n"
        "• <code>/analytics</code> или <code>/finance</code> — Детальная выручка (24ч, 7д, 30д), CR, рефералы\n"
        "• <code>/createpromo &lt;КОД&gt; &lt;СКИДКА%&gt; [ЛИМИТ]</code> — Создать промокод со скидкой\n"
        "• <code>/promos</code> — Список всех промокодов системы\n\n"
        "💬 <b>Анонимная поддержка и связь:</b>\n"
        "• <i>Просто ответьте (Reply) на любое сообщение тикета</i> — бот сам передаст ваш ответ клиенту!\n"
        "• <code>/reply &lt;tg_id&gt; &lt;текст&gt;</code> — Написать клиенту от лица бота\n"
        "• <code>/sendkey &lt;tg_id&gt; &lt;дни&gt; [ПК] [заметка]</code> — Выдать ключ и сразу доставить в ЛС\n"
        "• <code>/user &lt;tg_id&gt;</code> — Полное досье клиента (ключи, HWID, баланс)\n"
        "• <code>/ban &lt;tg_id&gt;</code> и <code>/unban &lt;tg_id&gt;</code> — Блокировка спамеров\n\n"
        "🔑 <b>Управление лицензиями:</b>\n"
        "• <code>/genkey &lt;дни&gt; [tg_id] [устройств]</code> — Выпустить ключ без отправки\n"
        "• <code>/info &lt;ключ&gt;</code> — Досье ключа, статус и привязанные ПК\n"
        "• <code>/extend &lt;ключ&gt; &lt;дни&gt;</code> — Продлить ключ на N дней\n"
        "• <code>/unbind &lt;ключ&gt;</code> — Сбросить привязку всех ПК ключа\n"
        "• <code>/revoke &lt;ключ&gt;</code> — Отозвать лицензию навсегда\n\n"
        "📢 <b>Рассылка и retention:</b>\n"
        "• <code>/announce &lt;текст&gt;</code> — Официальный анонс обновления всем клиентам\n"
        "• <code>/broadcast &lt;текст&gt;</code> — Обычная рассылка\n"
        "• <code>/check_expirations</code> — Ручной запуск проверки истекающих ключей\n\n"
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

    # Check referral param: /start ref_<tg_id>
    ref_tg_id = None
    if message.text:
        cmd_parts = message.text.split(maxsplit=1)
        if len(cmd_parts) > 1 and cmd_parts[1].startswith("ref_"):
            raw_ref = cmd_parts[1].replace("ref_", "").strip()
            if raw_ref.isdigit():
                ref_tg_id = int(raw_ref)

    async with AsyncSessionLocal() as db:
        stmt = select(User).where(User.telegram_id == tg_id)
        res = await db.execute(stmt)
        user = res.scalar_one_or_none()

        if not user:
            referrer_db_id = None
            if ref_tg_id and ref_tg_id != tg_id:
                ref_user = (await db.execute(select(User).where(User.telegram_id == ref_tg_id))).scalar_one_or_none()
                if ref_user:
                    referrer_db_id = ref_user.id
                    bot = get_bot_instance()
                    if bot:
                        try:
                            await bot.send_message(
                                ref_tg_id,
                                f"🤝 <b>Новый реферал!</b> Пользователь <b>{escape_html(first_name or 'Клиент')}</b> зарегистрировался по вашей ссылке.\n"
                                f"Вам будет начисляться <b>15% кэшбэка</b> со всех его покупок!",
                                parse_mode="HTML"
                            )
                        except Exception:
                            pass

            user = User(
                telegram_id=tg_id,
                username=username,
                first_name=first_name,
                referrer_id=referrer_db_id,
                balance=0
            )
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
        "• 💳 <b>Купить лицензию</b> — выбор тарифа и моментальная авто-оплата\n"
        "• 💻 <b>Мои устройства</b> — список ваших привязанных ПК с инфой об ОС\n"
        "• 🔄 <b>Перенести лицензию</b> — сброс HWID для нового ПК (150 ₽)\n"
        "• 🤝 <b>Партнерка</b> — зарабатывайте 15% с покупок приглашенных друзей\n"
        "• 🎟 <b>Промокод</b> — активация персональной скидки\n"
        "• ❓ <b>FAQ & Помощь</b> — интерактивная база знаний\n"
        "• 📢 <b>Что нового</b> — список улучшений в версии v2.0 PRO\n"
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
                [InlineKeyboardButton(text="🔄 Сбросить HWID для этого ключа (150 ₽)", callback_data=f"user_req_hwid_key:{lic.license_key}")],
                [InlineKeyboardButton(text="⚡ Инструкция по активации", callback_data="nav_how_activate")]
            ])
            await message.answer(card, reply_markup=kb, parse_mode="HTML")


@router.callback_query(F.data.startswith("user_req_hwid_key:"))
async def cb_user_req_hwid_key(callback: CallbackQuery):
    key = callback.data.split(":", 1)[1].strip()
    text = (
        f"🔄 <b>Заказ сброса HWID для ключа:</b> <code>{key}</code>\n\n"
        "💰 <b>Стоимость услуги:</b> <b>150 ₽</b>\n\n"
        "Сброс привязки необходим, если вы:\n"
        "• Переустановили Windows или отформатировали диск\n"
        "• Заменили материнскую плату, процессор или SSD\n"
        "• Хотите перенести программу на другой компьютер\n\n"
        "После подтверждения оплаты оператор моментально сбросит привязанные ПК, "
        "и вы сможете активировать ключ на новом оборудовании!"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="💳 Оплатить 150 ₽ (СБП / Карта)", callback_data=f"buy_tariff:hwid_key_{key}:150")],
        [InlineKeyboardButton(text="🔙 Назад к лицензиям", callback_data="nav_my_license")]
    ])
    await callback.message.answer(text, reply_markup=kb, parse_mode="HTML")
    await callback.answer()


@router.message(F.text == "💳 Купить лицензию")
async def handle_buy_license(message: Message):
    if await check_user_banned(message.from_user.id):
        return

    user_promo = _user_active_promo.get(message.from_user.id)
    discount_pct = user_promo.get("discount_percent", 0) if user_promo else 0
    promo_badge = f"🎟 <b>Активирован промокод {user_promo['code']} (-{discount_pct}%)!</b>\n" if discount_pct > 0 else ""

    text = (
        "💳 <b>Официальные тарифные планы WinCleaner:</b>\n\n"
        f"{promo_badge}"
        "• 🌟 <b>1 месяц — 299 ₽</b> (1 ПК)\n"
        "  └ Быстрый старт, полная очистка реестра, кэша и следов телеметрии.\n\n"
        "• 🚀 <b>1 год — 990 ₽</b> (1 ПК) — 🔥 <b>ХИТ ПРОДАЖ! (Экономия 72%)</b>\n"
        "  └ Целый год безупречной и быстрой работы вашей Windows + приоритет.\n\n"
        "• 👑 <b>Lifetime — 1 990 ₽</b> (2 ПК) — ♾️ <b>Навсегда!</b>\n"
        "  └ Бессрочный доступ для 2 ваших компьютеров, пожизненные обновления.\n\n"
        "• 🔄 <b>Сброс HWID / Перенос ПК — 150 ₽</b>\n"
        "  └ Перенос лицензии при смене оборудования или переустановке Windows.\n\n"
        "✨ <b>В каждый тариф входит:</b>\n"
        "✅ Удаление всего системного мусора и следов шпионажа\n"
        "✅ Оптимизация сети, пинга и оперативной памяти\n"
        "✅ Моментальная автоматическая выдача ключа сразу после оплаты\n"
        "✅ Полная техническая поддержка 24/7 прямо в этом боте\n\n"
        "Выберите желаемый тариф ниже ⬇️"
    )

    await send_banner_or_text(
        message,
        settings.banner_tariffs,
        text,
        reply_markup=get_tariffs_inline_keyboard(discount_pct)
    )


@router.callback_query(F.data == "nav_tariffs")
async def cb_nav_tariffs(callback: CallbackQuery):
    await callback.answer()
    await handle_buy_license(callback.message)


async def execute_successful_purchase(
    user_id: int,
    item_type: str,
    price_rub: int,
    payment_method: str,
    order_id: Optional[str] = None
):
    """
    Automated fulfillment:
    - Auto-issues license key or executes instant HWID unbind
    - Credits 15% cashback to referrer's balance if user was invited
    - Sends celebration cards to client and alerts admin
    """
    bot = get_bot_instance()
    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == user_id))
        user = u_res.scalar_one_or_none()
        if not user:
            user = User(telegram_id=user_id)
            db.add(user)
            await db.flush()

        # 1. Referral Cashback (15%)
        if user.referrer_id:
            ref_u = (await db.execute(select(User).where(User.id == user.referrer_id))).scalar_one_or_none()
            if ref_u:
                reward = max(1, int(price_rub * (REFERRAL_PERCENT / 100)))
                ref_u.balance = (ref_u.balance or 0) + reward
                ref_history = ReferralHistory(
                    referrer_id=ref_u.id,
                    referred_id=user.id,
                    reward_rub=reward
                )
                db.add(ref_history)
                if bot:
                    try:
                        await bot.send_message(
                            ref_u.telegram_id,
                            f"🎉 <b>Начисление бонуса!</b> Ваш приглашенный друг совершил оплату.\n"
                            f"💰 На ваш баланс зачислено: <b>+{reward} ₽</b>\n"
                            f"💳 Доступный баланс: <b>{ref_u.balance} ₽</b> (можно оплатить тариф или сброс HWID)",
                            parse_mode="HTML"
                        )
                    except Exception:
                        pass

        # 2. Fulfillment
        if "hwid" in item_type:
            # Unbind devices
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

            log = ActivationLog(
                license_id=None,
                hwid="PAID_AUTO_HWID",
                action="transfer",
                ip_address=payment_method,
                success=True,
                details=f"Auto unbind {unbound_count} device(s) via {payment_method}"
            )
            db.add(log)
            await db.commit()

            if bot:
                confirm_text = (
                    "🎉 <b>Оплата 150 ₽ успешно получена!</b>\n"
                    "━━━━━━━━━━━━━━━━━━━━━━\n"
                    "✅ <b>Привязка HWID моментально сброшена!</b>\n\n"
                    f"Отвязано активных компьютеров: <b>{unbound_count}</b>.\n"
                    "Теперь запустите WinCleaner на новом компьютере и вставьте ваш ключ — он активируется без задержек!"
                )
                kb = InlineKeyboardMarkup(inline_keyboard=[
                    [InlineKeyboardButton(text="🔑 Моя лицензия", callback_data="nav_my_license")]
                ])
                try:
                    await bot.send_message(user_id, confirm_text, reply_markup=kb, parse_mode="HTML")
                except Exception:
                    pass

            await send_admin_alert(
                f"✅ <b>Авто-оплата сброса HWID (150 ₽)!</b>\n"
                f"👤 Клиент: <code>{user_id}</code> | Метод: <b>{payment_method}</b>\n"
                f"Отвязано ПК: <b>{unbound_count}</b>"
            )
            return

        else:
            # Issue License
            days_map = {"month": 30, "year": 365, "lifetime": 36500}
            devs_map = {"month": 1, "year": 1, "lifetime": 2}
            base_type = item_type.replace("tariff_", "").split(":")[0]
            days = days_map.get(base_type, 30)
            max_devs = devs_map.get(base_type, 1)

            key = generate_key_string()
            now = utc_now()
            exp = now + timedelta(days=days)

            lic = License(
                license_key=key,
                user_id=user.id,
                status="active",
                max_devices=max_devs,
                created_at=now,
                expires_at=exp,
                notes=f"Auto-issued via {payment_method} ({price_rub} RUB)"
            )
            db.add(lic)
            await db.commit()

            exp_str = "Бессрочно (Lifetime)" if days > 10000 else exp.strftime("%d.%m.%Y")
            if bot:
                card = (
                    "🎉 <b>Оплата успешно подтверждена! Ваша лицензия готова:</b>\n"
                    "━━━━━━━━━━━━━━━━━━━━━━\n"
                    f"🔑 Ваш ключ: <code>{key}</code>\n"
                    f"📅 Срок действия: <b>{exp_str}</b>\n"
                    f"💻 Доступно компьютеров: <b>{max_devs} ПК</b>\n"
                    "━━━━━━━━━━━━━━━━━━━━━━\n\n"
                    "⚡ <b>Как активировать:</b>\n"
                    "1. Запустите <code>WinCleaner.exe</code> от имени администратора\n"
                    "2. Скопируйте ключ выше и вставьте в окно активации\n"
                    "3. Нажмите «⚡ Активировать»!\n\n"
                    "<i>(Нажмите на ключ выше, чтобы скопировать его в буфер обмена)</i>"
                )
                kb = InlineKeyboardMarkup(inline_keyboard=[
                    [InlineKeyboardButton(text="🔑 Моя лицензия", callback_data="nav_my_license")],
                    [InlineKeyboardButton(text="⚡ Инструкция", callback_data="nav_how_activate")]
                ])
                try:
                    await bot.send_message(user_id, card, reply_markup=kb, parse_mode="HTML")
                except Exception:
                    pass

            await send_admin_alert(
                f"💰 <b>НОВАЯ АВТО-ОПЛАТА ЛИЦЕНЗИИ!</b>\n"
                f"👤 Клиент: <code>{user_id}</code>\n"
                f"📦 Тариф: <b>{base_type}</b> ({price_rub} ₽)\n"
                f"💳 Метод: <b>{payment_method}</b>\n"
                f"🔑 Выдан ключ: <code>{key}</code>"
            )


@router.callback_query(F.data.startswith("buy_tariff:"))
async def cb_buy_tariff(callback: CallbackQuery):
    parts = callback.data.split(":")
    tariff_code = parts[1]
    price = int(parts[2]) if len(parts) > 2 and parts[2].isdigit() else 299

    tariff_titles = {
        "month": "1 месяц (1 ПК)",
        "year": "1 год (1 ПК) — Хит продаж",
        "lifetime": "Lifetime Навсегда (2 ПК)",
        "hwid": "Сброс HWID / Перенос ПК"
    }
    if tariff_code.startswith("hwid_key_"):
        k = tariff_code.replace("hwid_key_", "")
        tariff_name = f"Сброс HWID для {k}"
    elif tariff_code.startswith("hwid_"):
        dev_id = tariff_code.replace("hwid_", "")
        tariff_name = f"Сброс HWID (ПК #{dev_id})"
    else:
        tariff_name = tariff_titles.get(tariff_code, tariff_code)

    user_id = callback.from_user.id
    order_id = f"ORD{int(time.time())}{secrets.token_hex(2).upper()}"

    stars_map = {"month": 150, "year": 500, "lifetime": 990, "hwid": 75}
    stars_amount = stars_map.get(tariff_code.split("_")[0], 150)

    # Save pending order in DB
    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == user_id))
        user = u_res.scalar_one_or_none()
        user_balance = user.balance if user else 0

        ord_record = Order(
            order_id=order_id,
            user_id=user.id if user else None,
            user_telegram_id=user_id,
            item_type=tariff_code,
            amount_rub=price,
            stars_amount=stars_amount,
            payment_method="pending",
            status="pending"
        )
        db.add(ord_record)
        await db.commit()

    text = (
        f"🛍️ <b>Оформление заказа: {escape_html(tariff_name)}</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"💰 Сумма к оплате: <b>{price} ₽</b> (или <b>{stars_amount} ⭐ Stars</b>)\n"
        f"💳 Ваш баланс бонусов: <b>{user_balance} ₽</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n\n"
        "⚡ <b>Выберите удобный способ оплаты:</b>\n"
        "• ⭐ <b>Telegram Stars</b> — моментальная оплата в 1 клик прямо в Telegram с авто-выдачей ключа!\n"
        "• 💰 <b>С баланса бота</b> — оплатите заработанными бонусами от рефералов без реальных денег\n"
        "• 💳 <b>СБП / Карты РФ</b> — перевод через Систему быстрых платежей или банковскую карту\n"
        "• 💎 <b>Криптовалюта</b> — оплата через CryptoBot (USDT TRC-20, TON, BTC)\n\n"
        "<i>Ключ генерируется и отправляется вам сразу после подтверждения оплаты!</i>"
    )

    await callback.message.answer(
        text,
        reply_markup=get_payment_methods_keyboard(order_id, tariff_code, price, stars_amount),
        parse_mode="HTML"
    )
    await callback.answer()


@router.callback_query(F.data.startswith("pay_stars:"))
async def cb_pay_stars(callback: CallbackQuery):
    parts = callback.data.split(":")
    order_id = parts[1]
    tariff_code = parts[2]
    stars_amount = int(parts[3])
    price_rub = int(parts[4])

    tariff_titles = {
        "month": "Лицензия 1 месяц (1 ПК)",
        "year": "Лицензия 1 год (1 ПК)",
        "lifetime": "Лицензия Lifetime Навсегда (2 ПК)",
        "hwid": "Сброс привязки HWID (1 ПК)"
    }
    title = tariff_titles.get(tariff_code.split("_")[0], "Лицензия WinCleaner")

    try:
        await callback.message.answer_invoice(
            title=f"WinCleaner: {title}",
            description="Официальный ключ WinCleaner 2.0 PRO. Моментальная выдача ключа сразу после оплаты!",
            payload=f"stars:{order_id}:{tariff_code}:{price_rub}",
            currency="XTR",
            prices=[LabeledPrice(label=title, amount=stars_amount)],
            provider_token=""
        )
        await callback.answer("Счет на оплату Stars выставлен!")
    except Exception as e:
        logger.error(f"Failed to send Stars invoice: {e}")
        await callback.answer(f"Ошибка выставления счета: {e}", show_alert=True)


@router.pre_checkout_query()
async def process_pre_checkout_query(query: PreCheckoutQuery):
    """Pre-checkout validation required by Telegram Stars."""
    await query.answer(ok=True)


@router.message(F.successful_payment)
async def process_successful_payment(message: Message):
    """Handles completed Telegram Stars payment."""
    payment_info = message.successful_payment
    payload = payment_info.invoice_payload

    if payload and payload.startswith("stars:"):
        parts = payload.split(":")
        order_id = parts[1]
        tariff_code = parts[2]
        price_rub = int(parts[3])

        # Mark order paid
        async with AsyncSessionLocal() as db:
            await db.execute(
                update(Order).where(Order.order_id == order_id).values(
                    status="paid",
                    payment_method="telegram_stars",
                    paid_at=utc_now()
                )
            )
            await db.commit()

        # Fulfill
        await execute_successful_purchase(
            user_id=message.from_user.id,
            item_type=tariff_code,
            price_rub=price_rub,
            payment_method="Telegram Stars",
            order_id=order_id
        )


@router.callback_query(F.data.startswith("pay_balance:"))
async def cb_pay_balance(callback: CallbackQuery):
    parts = callback.data.split(":")
    order_id = parts[1]
    tariff_code = parts[2]
    price_rub = int(parts[3])

    user_id = callback.from_user.id
    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == user_id))
        user = u_res.scalar_one_or_none()

        if not user or (user.balance or 0) < price_rub:
            cur_bal = user.balance if user else 0
            await callback.answer(
                f"❌ Недостаточно средств на балансе!\nУ вас: {cur_bal} ₽, нужно: {price_rub} ₽.\n\n"
                "Приглашайте друзей через раздел «🤝 Партнерка», чтобы заработать на оплату!",
                show_alert=True
            )
            return

        # Deduct balance
        user.balance -= price_rub
        await db.execute(
            update(Order).where(Order.order_id == order_id).values(
                status="paid",
                payment_method="balance",
                paid_at=utc_now()
            )
        )
        await db.commit()

    await callback.answer("✅ Оплата с баланса списана!", show_alert=True)
    await execute_successful_purchase(
        user_id=user_id,
        item_type=tariff_code,
        price_rub=price_rub,
        payment_method="Внутренний баланс",
        order_id=order_id
    )


@router.callback_query(F.data.startswith("pay_sbp:"))
async def cb_pay_sbp(callback: CallbackQuery):
    parts = callback.data.split(":")
    order_id = parts[1]
    price_rub = int(parts[3])

    text = (
        f"💳 <b>Оплата через СБП / Карту РФ (Заказ <code>{order_id}</code>)</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"💰 Сумма к переводу: <b>{price_rub} ₽</b> (ровно)\n\n"
        "📱 <b>Реквизиты для оплаты:</b>\n"
        "• СБП / Номер телефона: <code>+7 999 000-00-00</code>\n"
        "• Банк-получатель: <b>Т-Банк (Тинькофф) / Сбер</b>\n"
        f"• В комментарии к платежу укажите: <code>{order_id}</code>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n\n"
        "💬 <b>Как подтвердить оплату?</b>\n"
        "Отправьте скриншот чека прямо в этот диалог. Оператор сразу проверит его и подтвердит выдачу ключа!"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="✍️ Отправить чек об оплате", callback_data="user_start_ticket")],
        [InlineKeyboardButton(text="🔙 Назад к способам оплаты", callback_data="nav_tariffs")]
    ])
    await callback.message.answer(text, reply_markup=kb, parse_mode="HTML")
    await callback.answer()


@router.callback_query(F.data.startswith("pay_crypto:"))
async def cb_pay_crypto(callback: CallbackQuery):
    parts = callback.data.split(":")
    order_id = parts[1]
    price_rub = int(parts[3])

    text = (
        f"💎 <b>Оплата криптовалютой (Заказ <code>{order_id}</code>)</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"💰 Сумма к оплате: <b>{price_rub} ₽</b> (в эквиваленте USDT/TON)\n\n"
        "• <b>USDT (TRC-20):</b>\n<code>TWinCleanerOfficialVault999888777</code>\n\n"
        "• <b>TON (The Open Network):</b>\n<code>EQ_WinCleaner_Official_Ton_Wallet_99</code>\n\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "💬 После отправки прикрепите хэш транзакции или скриншот прямо в этот чат!"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="✍️ Сообщить об оплате", callback_data="user_start_ticket")],
        [InlineKeyboardButton(text="🔙 Назад к тарифам", callback_data="nav_tariffs")]
    ])
    await callback.message.answer(text, reply_markup=kb, parse_mode="HTML")
    await callback.answer()


@router.message(F.text.in_({"🤝 Партнерка", "🤝 Реферальная программа", "/ref", "/partner"}))
async def handle_partner(message: Message):
    tg_id = message.from_user.id
    if await check_user_banned(tg_id):
        return

    bot_user = settings.BOT_USERNAME or "WinCleanerLicense_Bot"
    ref_link = f"https://t.me/{bot_user}?start=ref_{tg_id}"

    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == tg_id))
        user = u_res.scalar_one_or_none()
        if not user:
            user = User(telegram_id=tg_id, balance=0)
            db.add(user)
            await db.commit()

        ref_count = (await db.execute(select(func.count(User.id)).where(User.referrer_id == user.id))).scalar() or 0
        total_earned = (await db.execute(
            select(func.coalesce(func.sum(ReferralHistory.reward_rub), 0)).where(ReferralHistory.referrer_id == user.id)
        )).scalar() or 0
        cur_balance = user.balance or 0

    text = (
        "🤝 <b>Партнерская программа WinCleaner («Приведи друга»):</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "Делитесь вашей персональной ссылкой с друзьями, в соцсетях и каналах! "
        f"Вы будете получать <b>{REFERRAL_PERCENT}% кэшбэка</b> с абсолютно каждой покупки вашего реферала!\n\n"
        f"🔗 <b>Ваша реферальная ссылка:</b>\n"
        f"<code>{ref_link}</code>\n\n"
        "📊 <b>Ваша статистика:</b>\n"
        f"• 👥 Приглашено друзей: <b>{ref_count}</b>\n"
        f"• 💰 Всего заработано: <b>{total_earned} ₽</b>\n"
        f"• 💳 Доступный баланс: <b>{cur_balance} ₽</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n\n"
        "💡 <i>Накопленные рубли можно тратить на покупку любых лицензий и сброс HWID прямо в боте!</i>"
    )

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="💳 Оплатить тариф с баланса", callback_data="nav_tariffs")],
        [InlineKeyboardButton(text="🔄 Сброс HWID с баланса (150 ₽)", callback_data="buy_tariff:hwid:150")],
        [InlineKeyboardButton(text="📤 Запросить вывод в саппорт", callback_data="user_start_ticket")]
    ])

    await message.answer(text, reply_markup=kb, parse_mode="HTML")


@router.message(F.text.in_({"🎟 Промокод", "🎟 Ввести промокод", "/promo"}))
@router.callback_query(F.data == "user_enter_promo")
async def handle_promo_menu(event):
    message = event if isinstance(event, Message) else event.message
    user_id = event.from_user.id
    if await check_user_banned(user_id):
        return

    _user_waiting_promo.add(user_id)
    active_promo = _user_active_promo.get(user_id)

    status_str = ""
    if active_promo:
        status_str = f"✅ У вас уже активен промокод <code>{active_promo['code']}</code> (Скидка <b>{active_promo['discount_percent']}%</b>).\n\n"

    text = (
        "🎟 <b>Система промокодов WinCleaner:</b>\n\n"
        f"{status_str}"
        "Отправьте ваш промокод ответным сообщением в этот чат (например: <code>START20</code> или <code>RENEW10</code>).\n\n"
        "Скидка моментально применится ко всем тарифам!"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔙 Отмена", callback_data="nav_tariffs")]
    ])
    await message.answer(text, reply_markup=kb, parse_mode="HTML")
    if isinstance(event, CallbackQuery):
        await event.answer()


@router.message(F.text.in_({"❓ FAQ & Помощь", "❓ FAQ", "❓ Частые вопросы", "/faq"}))
@router.callback_query(F.data == "user_faq")
async def handle_faq_menu(event):
    message = event if isinstance(event, Message) else event.message
    text = (
        "❓ <b>Интерактивная база знаний WinCleaner:</b>\n\n"
        "Выберите интересующий вас вопрос ниже, чтобы мгновенно получить подробный ответ специалиста:"
    )
    if isinstance(event, CallbackQuery):
        try:
            await event.message.edit_text(text, reply_markup=get_faq_inline_keyboard(), parse_mode="HTML")
        except Exception:
            await event.message.answer(text, reply_markup=get_faq_inline_keyboard(), parse_mode="HTML")
        await event.answer()
    else:
        await message.answer(text, reply_markup=get_faq_inline_keyboard(), parse_mode="HTML")


@router.callback_query(F.data.startswith("faq_item:"))
async def cb_faq_item(callback: CallbackQuery):
    topic = callback.data.split(":")[1]
    answers = {
        "antivirus": (
            "🛡️ <b>Почему Windows Defender или антивирус ругается на WinCleaner?</b>\n\n"
            "WinCleaner выполняет глубокую очистку реестра, останавливает скрытые службы слежки Microsoft "
            "и настраивает сетевые интерфейсы на уровне ядра Windows.\n\n"
            "Поскольку программа взаимодействует с системными ветками реестра (HKLM/HKCU), антивирусы могут выдать "
            "ложное срабатывание (False Positive).\n\n"
            "✅ <b>Решение:</b>\n"
            "1. Запускайте <code>WinCleaner.exe</code> от имени администратора.\n"
            "2. Добавьте папку программы в исключения Windows Defender (Защита от вирусов ➔ Исключения)."
        ),
        "cleaning": (
            "⚡ <b>Как правильно запускать глубокую очистку?</b>\n\n"
            "1. Закройте все браузеры и игры перед началом очистки, чтобы освободить кэш-файлы.\n"
            "2. Нажмите «Полное сканирование» в главном окне WinCleaner.\n"
            "3. После анализа нажмите «Очистить и ускорить».\n"
            "4. Для применения сетевых твиков и очистки ядра рекомендуется перезагрузить компьютер."
        ),
        "hwid": (
            "🔄 <b>Как перенести лицензию на новый компьютер или после переустановки Windows?</b>\n\n"
            "Каждая лицензия защищена криптографической привязкой к HWID вашего компьютера.\n\n"
            "Если вы переустановили Windows, заменили материнскую плату, диск или купили новый ПК:\n"
            "1. Перейдите в раздел <b>«🔄 Перенести лицензию»</b> в боте.\n"
            "2. Оплатите сброс привязки HWID (<b>150 ₽</b>).\n"
            "3. Привязка автоматически сбросится, и вы сможете сразу активировать ключ на новом ПК!"
        ),
        "keys": (
            "🔑 <b>Где взять ключ и как его активировать?</b>\n\n"
            "1. Выберите подходящий тариф в меню <b>«💳 Купить лицензию»</b>.\n"
            "2. Оплатите через Stars, СБП, карту или баланс.\n"
            "3. Бот сразу выдаст ключ вида <code>WINC-XXXX-XXXX-XXXX</code>.\n"
            "4. Запустите <code>WinCleaner.exe</code> и вставьте ключ в окно авторизации."
        ),
        "errors": (
            "⚙️ <b>Решение распространенных ошибок:</b>\n\n"
            "• <b>Ошибка «Ключ не найден»:</b> Проверьте, чтобы в ключе не было лишних пробелов в начале или конце.\n"
            "• <b>Ошибка «Превышен лимит устройств»:</b> Перейдите в «💻 Мои устройства» или «🔄 Перенести лицензию» для сброса старого ПК.\n"
            "• <b>Ошибка «Нет связи с сервером»:</b> Проверьте интернет-соединение. Программе нужен интернет только на 2 секунды во время активации."
        )
    }

    ans = answers.get(topic, "Информация обновляется.")
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔙 Назад к списку вопросов", callback_data="user_faq")],
        [InlineKeyboardButton(text="💬 Задать свой вопрос поддержке", callback_data="user_start_ticket")]
    ])
    try:
        await callback.message.edit_text(ans, reply_markup=kb, parse_mode="HTML")
    except Exception:
        await callback.message.answer(ans, reply_markup=kb, parse_mode="HTML")
    await callback.answer()


# ===========================================================================
# Community Feature Voting Engine
# ===========================================================================

def get_idea_status_badge(status: str) -> str:
    if status == "in_progress":
        return "🛠️ <b>В разработке</b>"
    elif status == "completed":
        return "✅ <b>Реализовано в v2.0</b>"
    elif status == "rejected":
        return "❌ <b>Отклонено</b>"
    return "🟢 <b>На голосовании</b>"


async def ensure_initial_ideas(db):
    """Seeds popular community suggestions on fresh databases so the catalog is ready to explore."""
    count = (await db.execute(select(func.count(FeatureIdea.id)))).scalar() or 0
    if count == 0:
        seed_items = [
            FeatureIdea(
                user_id=None,
                user_telegram_id=1,
                author_name="Алексей (Сообщество)",
                title="Очистка кэша Discord, Steam и Epic Games",
                description="Добавить модуль глубокой очистки временных файлов и кэша игровых клиентов (Discord, Steam shader cache, Epic Games Launcher, Riot Client), которые занимают гигабайты на SSD.",
                votes_count=18,
                status="in_progress"
            ),
            FeatureIdea(
                user_id=None,
                user_telegram_id=1,
                author_name="Максим (PRO-пользователь)",
                title="Оптимизация оперативной памяти (Standby List Cleaner)",
                description="Встроить алгоритм автоматического сброса рабочего набора и standby-кэша оперативной памяти перед запуском игр для устранения микрофризов и статтеров в Windows 10/11.",
                votes_count=24,
                status="in_progress"
            ),
            FeatureIdea(
                user_id=None,
                user_telegram_id=1,
                author_name="Дмитрий",
                title="Автозапуск фоновой очистки по расписанию",
                description="Возможность настроить еженедельную тихую очистку системы в фоне без всплывающих окон, с записью лога в журнал.",
                votes_count=12,
                status="approved"
            ),
            FeatureIdea(
                user_id=None,
                user_telegram_id=1,
                author_name="Сергей",
                title="Глубокая деинсталляция программ и остатков в реестре",
                description="Инструмент для полного удаления ненужного ПО с принудительным сканированием оставшихся ключей реестра и скрытых папок в AppData/ProgramData.",
                votes_count=15,
                status="approved"
            ),
        ]
        db.add_all(seed_items)
        await db.commit()


async def render_ideas_hub(db, user_telegram_id: int) -> Tuple[str, InlineKeyboardMarkup]:
    """Builds the main community voting hub overview."""
    await ensure_initial_ideas(db)

    # Top 5 proposals
    top_stmt = select(FeatureIdea).where(FeatureIdea.status != "rejected").order_by(
        FeatureIdea.votes_count.desc(), FeatureIdea.id.desc()
    ).limit(5)
    top_ideas = (await db.execute(top_stmt)).scalars().all()

    user_ideas_count = (await db.execute(
        select(func.count(FeatureIdea.id)).where(FeatureIdea.user_telegram_id == user_telegram_id)
    )).scalar() or 0

    top_lines = []
    for idx, idea in enumerate(top_ideas, 1):
        badge = "🛠️" if idea.status == "in_progress" else ("✅" if idea.status == "completed" else "🟢")
        top_lines.append(f"{idx}. {badge} <b>{escape_html(idea.title)}</b> — <b>{idea.votes_count}</b> 👍")
    top_text = "\n".join(top_lines) if top_lines else "<i>Пока предложений нет. Будьте первым!</i>"

    text = (
        "💡 <b>ГОЛОСОВАНИЕ ЗА НОВЫЕ ФУНКЦИИ WinCleaner</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "Здесь пользователи предлагают свои идеи для программы, а сообщество голосует за самые нужные!\n\n"
        "Функции с наибольшим количеством голосов попадают в приоритетную разработку наших инженеров. 🛠️\n\n"
        "🏆 <b>Топ предложений по голосам:</b>\n"
        f"{top_text}\n\n"
        f"⭐️ <i>Ваших предложенных идей: <b>{user_ideas_count}</b></i>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "👇 <i>Листайте предложения и голосуйте или предложите свою идею!</i>"
    )

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [
            InlineKeyboardButton(text="🔥 Смотреть идеи и голосовать", callback_data="ideas_browse:0")
        ],
        [
            InlineKeyboardButton(text="✍️ Предложить функцию", callback_data="idea_propose"),
            InlineKeyboardButton(text="⭐️ Мои идеи", callback_data="ideas_my:0")
        ],
        [
            InlineKeyboardButton(text="🔄 Обновить список", callback_data="ideas_hub_refresh")
        ]
    ])
    return text, kb


async def render_idea_card(db, user_telegram_id: int, offset: int = 0, filter_user_only: bool = False) -> Tuple[str, InlineKeyboardMarkup]:
    """Renders a single interactive idea card with carousel navigation and voting."""
    await ensure_initial_ideas(db)

    if filter_user_only:
        count_stmt = select(func.count(FeatureIdea.id)).where(FeatureIdea.user_telegram_id == user_telegram_id)
        query = select(FeatureIdea).where(FeatureIdea.user_telegram_id == user_telegram_id).order_by(FeatureIdea.id.desc())
    else:
        count_stmt = select(func.count(FeatureIdea.id)).where(FeatureIdea.status != "rejected")
        query = select(FeatureIdea).where(FeatureIdea.status != "rejected").order_by(
            FeatureIdea.votes_count.desc(), FeatureIdea.id.desc()
        )

    total = (await db.execute(count_stmt)).scalar() or 0
    if total == 0:
        if filter_user_only:
            msg = (
                "⭐️ <b>У вас пока нет предложенных идей.</b>\n\n"
                "Вы можете предложить новую функцию для WinCleaner прямо сейчас!"
            )
        else:
            msg = (
                "💡 <b>Список идей пока пуст.</b>\n\n"
                "Станьте первым, кто предложит крутую функцию для WinCleaner!"
            )
        kb = InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="✍️ Предложить функцию", callback_data="idea_propose")],
            [InlineKeyboardButton(text="🔙 Меню идей", callback_data="ideas_hub")]
        ])
        return msg, kb

    offset = offset % total
    idea = (await db.execute(query.offset(offset).limit(1))).scalar_one_or_none()
    if not idea:
        return "❌ Идея не найдена.", InlineKeyboardMarkup(inline_keyboard=[[InlineKeyboardButton(text="🔙 Меню идей", callback_data="ideas_hub")]])

    # Check vote
    v_res = await db.execute(
        select(FeatureVote).where(FeatureVote.idea_id == idea.id, FeatureVote.user_telegram_id == user_telegram_id)
    )
    has_voted = v_res.scalar_one_or_none() is not None

    status_badge = get_idea_status_badge(idea.status)
    voted_str = "✅ <i>Вы поддержали эту идею (+1 👍)</i>" if has_voted else "🤍 <i>Вы еще не голосовали за эту идею</i>"
    created_str = idea.created_at.strftime("%d.%m.%Y")

    card_text = (
        f"💡 <b>Идея #{idea.id}: {escape_html(idea.title)}</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"📝 <b>Описание:</b>\n{escape_html(idea.description)}\n\n"
        f"👤 <b>Автор:</b> {escape_html(idea.author_name)}\n"
        f"📅 <b>Дата:</b> {created_str}\n"
        f"📊 <b>Статус:</b> {status_badge}\n"
        f"👍 <b>Поддержали:</b> <b>{idea.votes_count}</b> чел.\n"
        f"{voted_str}\n"
        "━━━━━━━━━━━━━━━━━━━━━━"
    )

    mode_prefix = "ideas_my" if filter_user_only else "ideas_browse"
    is_user_flag = "1" if filter_user_only else "0"

    rows = []
    # Vote / Unvote button
    if has_voted:
        rows.append([InlineKeyboardButton(text="✅ Вы поддержали (отменить?)", callback_data=f"idea_unvote:{idea.id}:{offset}:{is_user_flag}")])
    else:
        rows.append([InlineKeyboardButton(text="👍 Проголосовать (+1)", callback_data=f"idea_vote:{idea.id}:{offset}:{is_user_flag}")])

    # Carousel
    prev_off = (offset - 1) % total
    next_off = (offset + 1) % total
    rows.append([
        InlineKeyboardButton(text="⬅️ Пред.", callback_data=f"{mode_prefix}:{prev_off}"),
        InlineKeyboardButton(text=f"📌 {offset + 1}/{total}", callback_data="ideas_noop"),
        InlineKeyboardButton(text="След. ➡️", callback_data=f"{mode_prefix}:{next_off}")
    ])

    rows.append([
        InlineKeyboardButton(text="✍️ Предложить функцию", callback_data="idea_propose"),
        InlineKeyboardButton(text="🔙 Меню идей", callback_data="ideas_hub")
    ])

    return card_text, InlineKeyboardMarkup(inline_keyboard=rows)


@router.message(F.text.in_({"💡 Голосование", "💡 Идеи и Голосование", "Голосование", "/ideas", "/vote", "/suggest"}))
async def handle_ideas_entry(message: Message):
    if await check_user_banned(message.from_user.id):
        return
    _user_waiting_idea.discard(message.from_user.id)
    async with AsyncSessionLocal() as db:
        text, kb = await render_ideas_hub(db, message.from_user.id)
    await message.answer(text, reply_markup=kb, parse_mode="HTML")


@router.callback_query(F.data.in_({"ideas_hub", "ideas_hub_refresh"}))
async def cb_ideas_hub(callback: CallbackQuery):
    if await check_user_banned(callback.from_user.id):
        await callback.answer("⛔ Доступ ограничен.", show_alert=True)
        return
    _user_waiting_idea.discard(callback.from_user.id)
    async with AsyncSessionLocal() as db:
        text, kb = await render_ideas_hub(db, callback.from_user.id)
    try:
        await callback.message.edit_text(text, reply_markup=kb, parse_mode="HTML")
    except Exception:
        await callback.message.answer(text, reply_markup=kb, parse_mode="HTML")
    await callback.answer("Список обновлен" if callback.data == "ideas_hub_refresh" else None)


@router.callback_query(F.data.startswith("ideas_browse:"))
async def cb_ideas_browse(callback: CallbackQuery):
    if await check_user_banned(callback.from_user.id):
        await callback.answer("⛔ Доступ ограничен.", show_alert=True)
        return
    parts = callback.data.split(":")
    offset = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else 0
    async with AsyncSessionLocal() as db:
        text, kb = await render_idea_card(db, callback.from_user.id, offset=offset, filter_user_only=False)
    try:
        await callback.message.edit_text(text, reply_markup=kb, parse_mode="HTML")
    except Exception:
        await callback.message.answer(text, reply_markup=kb, parse_mode="HTML")
    await callback.answer()


@router.callback_query(F.data.startswith("ideas_my:"))
async def cb_ideas_my(callback: CallbackQuery):
    if await check_user_banned(callback.from_user.id):
        await callback.answer("⛔ Доступ ограничен.", show_alert=True)
        return
    parts = callback.data.split(":")
    offset = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else 0
    async with AsyncSessionLocal() as db:
        text, kb = await render_idea_card(db, callback.from_user.id, offset=offset, filter_user_only=True)
    try:
        await callback.message.edit_text(text, reply_markup=kb, parse_mode="HTML")
    except Exception:
        await callback.message.answer(text, reply_markup=kb, parse_mode="HTML")
    await callback.answer()


@router.callback_query(F.data == "ideas_noop")
async def cb_ideas_noop(callback: CallbackQuery):
    await callback.answer()


@router.callback_query(F.data.startswith("idea_vote:"))
async def cb_idea_vote(callback: CallbackQuery):
    user_id = callback.from_user.id
    if await check_user_banned(user_id):
        await callback.answer("⛔ Доступ ограничен.", show_alert=True)
        return

    parts = callback.data.split(":")
    if len(parts) < 3:
        await callback.answer("Ошибка запроса.")
        return
    idea_id = int(parts[1])
    offset = int(parts[2]) if parts[2].isdigit() else 0
    filter_user = (parts[3] == "1") if len(parts) > 3 else False

    async with AsyncSessionLocal() as db:
        idea_res = await db.execute(select(FeatureIdea).where(FeatureIdea.id == idea_id))
        idea = idea_res.scalar_one_or_none()
        if not idea:
            await callback.answer("Идея не найдена.", show_alert=True)
            return

        v_res = await db.execute(select(FeatureVote).where(FeatureVote.idea_id == idea_id, FeatureVote.user_telegram_id == user_id))
        existing_vote = v_res.scalar_one_or_none()

        if existing_vote:
            await callback.answer("Вы уже проголосовали за эту идею! 👍", show_alert=False)
        else:
            new_vote = FeatureVote(idea_id=idea_id, user_telegram_id=user_id)
            db.add(new_vote)
            idea.votes_count += 1
            await db.commit()
            await callback.answer("Спасибо! Ваш голос учтен! 🎉")

        text, kb = await render_idea_card(db, user_id, offset=offset, filter_user_only=filter_user)

    try:
        await callback.message.edit_text(text, reply_markup=kb, parse_mode="HTML")
    except Exception:
        pass


@router.callback_query(F.data.startswith("idea_unvote:"))
async def cb_idea_unvote(callback: CallbackQuery):
    user_id = callback.from_user.id
    if await check_user_banned(user_id):
        await callback.answer("⛔ Доступ ограничен.", show_alert=True)
        return

    parts = callback.data.split(":")
    if len(parts) < 3:
        await callback.answer("Ошибка запроса.")
        return
    idea_id = int(parts[1])
    offset = int(parts[2]) if parts[2].isdigit() else 0
    filter_user = (parts[3] == "1") if len(parts) > 3 else False

    async with AsyncSessionLocal() as db:
        idea_res = await db.execute(select(FeatureIdea).where(FeatureIdea.id == idea_id))
        idea = idea_res.scalar_one_or_none()

        v_res = await db.execute(select(FeatureVote).where(FeatureVote.idea_id == idea_id, FeatureVote.user_telegram_id == user_id))
        existing_vote = v_res.scalar_one_or_none()

        if existing_vote:
            await db.delete(existing_vote)
            if idea:
                idea.votes_count = max(0, idea.votes_count - 1)
            await db.commit()
            await callback.answer("Голос отозван.")
        else:
            await callback.answer("Вы еще не голосовали за эту идею.")

        text, kb = await render_idea_card(db, user_id, offset=offset, filter_user_only=filter_user)

    try:
        await callback.message.edit_text(text, reply_markup=kb, parse_mode="HTML")
    except Exception:
        pass


@router.callback_query(F.data == "idea_propose")
async def cb_idea_propose(callback: CallbackQuery):
    user_id = callback.from_user.id
    if await check_user_banned(user_id):
        await callback.answer("⛔ Доступ ограничен.", show_alert=True)
        return

    _user_waiting_idea.add(user_id)
    text = (
        "✍️ <b>Предложите новую функцию для WinCleaner!</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "Опишите вашу идею прямо в чат ответным сообщением:\n"
        "• Какую полезную функцию или твик вы хотите видеть в программе?\n"
        "• Какую проблему это решит?\n\n"
        "<i>Отправьте сообщение с описанием вашей идеи (или нажмите кнопку отмены ниже):</i>"
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="❌ Отмена", callback_data="idea_cancel_propose")]
    ])
    try:
        await callback.message.edit_text(text, reply_markup=kb, parse_mode="HTML")
    except Exception:
        await callback.message.answer(text, reply_markup=kb, parse_mode="HTML")
    await callback.answer()


@router.callback_query(F.data == "idea_cancel_propose")
async def cb_idea_cancel_propose(callback: CallbackQuery):
    user_id = callback.from_user.id
    _user_waiting_idea.discard(user_id)
    async with AsyncSessionLocal() as db:
        text, kb = await render_ideas_hub(db, user_id)
    try:
        await callback.message.edit_text(text, reply_markup=kb, parse_mode="HTML")
    except Exception:
        await callback.message.answer(text, reply_markup=kb, parse_mode="HTML")
    await callback.answer("Предложение идеи отменено.")


# ===========================================================================
# Admin Idea Moderation Callbacks & Commands
# ===========================================================================

@router.callback_query(F.data.startswith("adm_idea_st:"))
async def cb_admin_idea_status(callback: CallbackQuery):
    if not is_admin(callback.from_user.id):
        await callback.answer("⛔ Требуются права администратора.", show_alert=True)
        return

    parts = callback.data.split(":")
    if len(parts) < 3:
        await callback.answer("Ошибка параметров.")
        return
    idea_id = int(parts[1])
    new_status = parts[2]

    async with AsyncSessionLocal() as db:
        res = await db.execute(select(FeatureIdea).where(FeatureIdea.id == idea_id))
        idea = res.scalar_one_or_none()
        if not idea:
            await callback.answer("Идея не найдена.", show_alert=True)
            return

        idea.status = new_status
        await db.commit()

    badge = get_idea_status_badge(new_status)
    await callback.answer(f"Статус идеи #{idea_id} обновлен!")
    try:
        await callback.message.edit_caption(
            caption=(callback.message.caption or callback.message.text or "") + f"\n\n⚙️ <i>Статус изменен: {badge}</i>",
            parse_mode="HTML"
        )
    except Exception:
        try:
            await callback.message.edit_text(
                text=(callback.message.text or "") + f"\n\n⚙️ <i>Статус изменен: {badge}</i>",
                parse_mode="HTML"
            )
        except Exception:
            pass


@router.callback_query(F.data.startswith("adm_idea_del:"))
async def cb_admin_idea_delete(callback: CallbackQuery):
    if not is_admin(callback.from_user.id):
        await callback.answer("⛔ Требуются права администратора.", show_alert=True)
        return

    parts = callback.data.split(":")
    if len(parts) < 2:
        await callback.answer("Ошибка параметров.")
        return
    idea_id = int(parts[1])

    async with AsyncSessionLocal() as db:
        res = await db.execute(select(FeatureIdea).where(FeatureIdea.id == idea_id))
        idea = res.scalar_one_or_none()
        if idea:
            await db.delete(idea)
            await db.commit()

    await callback.answer(f"Идея #{idea_id} успешно удалена.")
    try:
        await callback.message.edit_text(f"🗑️ <b>Идея #{idea_id} удалена из базы данных.</b>", parse_mode="HTML")
    except Exception:
        pass


@router.callback_query(F.data == "admin_ideas_list")
async def cb_admin_ideas_list(callback: CallbackQuery):
    if not is_admin(callback.from_user.id):
        await callback.answer("⛔ Доступ ограничен.", show_alert=True)
        return

    async with AsyncSessionLocal() as db:
        stmt = select(FeatureIdea).order_by(FeatureIdea.votes_count.desc(), FeatureIdea.id.desc()).limit(10)
        ideas = (await db.execute(stmt)).scalars().all()

    if not ideas:
        await callback.answer("Список идей пуст.", show_alert=True)
        return

    lines = ["💡 <b>СПИСОК ИДЕЙ СООБЩЕСТВА (ТОП 10):</b>\n━━━━━━━━━━━━━━━━━━━━━━"]
    kb_rows = []
    for idea in ideas:
        badge = "🛠️" if idea.status == "in_progress" else ("✅" if idea.status == "completed" else ("❌" if idea.status == "rejected" else "🟢"))
        lines.append(f"• <b>#{idea.id}</b> {badge} {escape_html(idea.title)} — <b>{idea.votes_count}</b> 👍")
        kb_rows.append([
            InlineKeyboardButton(text=f"⚙️ #{idea.id} 🛠️ В работу", callback_data=f"adm_idea_st:{idea.id}:in_progress"),
            InlineKeyboardButton(text="✅ Готово", callback_data=f"adm_idea_st:{idea.id}:completed"),
            InlineKeyboardButton(text="🗑️", callback_data=f"adm_idea_del:{idea.id}")
        ])

    lines.append("\nКоманды:\n• <code>/set_idea_status &lt;id&gt; &lt;approved|in_progress|completed|rejected&gt;</code>\n• <code>/delete_idea &lt;id&gt;</code>")
    kb_rows.append([InlineKeyboardButton(text="🔙 Панель управления", callback_data="admin_panel_refresh")])

    try:
        await callback.message.edit_text("\n".join(lines), reply_markup=InlineKeyboardMarkup(inline_keyboard=kb_rows), parse_mode="HTML")
    except Exception:
        await callback.message.answer("\n".join(lines), reply_markup=InlineKeyboardMarkup(inline_keyboard=kb_rows), parse_mode="HTML")
    await callback.answer()


@router.message(Command("admin_ideas"))
async def cmd_admin_ideas(message: Message):
    if not is_admin(message.from_user.id):
        return

    async with AsyncSessionLocal() as db:
        stmt = select(FeatureIdea).order_by(FeatureIdea.votes_count.desc(), FeatureIdea.id.desc()).limit(15)
        ideas = (await db.execute(stmt)).scalars().all()

    if not ideas:
        await message.answer("💡 Предложенных идей пока нет.")
        return

    lines = ["💡 <b>ИДЕИ СООБЩЕСТВА WinCleaner:</b>\n━━━━━━━━━━━━━━━━━━━━━━"]
    for idea in ideas:
        badge = get_idea_status_badge(idea.status)
        lines.append(
            f"<b>#{idea.id}</b> — <b>{escape_html(idea.title)}</b>\n"
            f"👤 {escape_html(idea.author_name)} (<code>{idea.user_telegram_id}</code>) | 👍 <b>{idea.votes_count}</b> | {badge}\n"
        )
    lines.append("Управление: <code>/set_idea_status &lt;id&gt; &lt;статус&gt;</code> или <code>/delete_idea &lt;id&gt;</code>")
    await message.answer("\n".join(lines), parse_mode="HTML")


@router.message(Command("set_idea_status"))
async def cmd_set_idea_status(message: Message):
    if not is_admin(message.from_user.id):
        return
    parts = (message.text or "").strip().split(maxsplit=2)
    if len(parts) < 3 or not parts[1].isdigit():
        await message.answer("⚠️ Формат: <code>/set_idea_status &lt;id&gt; &lt;approved|in_progress|completed|rejected&gt;</code>", parse_mode="HTML")
        return

    idea_id = int(parts[1])
    new_status = parts[2].lower()
    if new_status not in {"approved", "in_progress", "completed", "rejected"}:
        await message.answer("⚠️ Допустимые статусы: <code>approved</code>, <code>in_progress</code>, <code>completed</code>, <code>rejected</code>", parse_mode="HTML")
        return

    async with AsyncSessionLocal() as db:
        res = await db.execute(select(FeatureIdea).where(FeatureIdea.id == idea_id))
        idea = res.scalar_one_or_none()
        if not idea:
            await message.answer(f"❌ Идея #{idea_id} не найдена.")
            return
        idea.status = new_status
        await db.commit()

    badge = get_idea_status_badge(new_status)
    await message.answer(f"✅ Статус идеи <b>#{idea_id}</b> изменен на {badge}.", parse_mode="HTML")


@router.message(Command("delete_idea"))
async def cmd_delete_idea(message: Message):
    if not is_admin(message.from_user.id):
        return
    parts = (message.text or "").strip().split(maxsplit=1)
    if len(parts) < 2 or not parts[1].isdigit():
        await message.answer("⚠️ Формат: <code>/delete_idea &lt;id&gt;</code>", parse_mode="HTML")
        return
    idea_id = int(parts[1])
    async with AsyncSessionLocal() as db:
        res = await db.execute(select(FeatureIdea).where(FeatureIdea.id == idea_id))
        idea = res.scalar_one_or_none()
        if not idea:
            await message.answer(f"❌ Идея #{idea_id} не найдена.")
            return
        await db.delete(idea)
        await db.commit()
    await message.answer(f"🗑️ Идея <b>#{idea_id}</b> удалена из базы данных.", parse_mode="HTML")


@router.message(F.text.in_({"📢 Что нового", "Что нового", "/changelog", "/updates"}))
async def handle_whats_new(message: Message):
    if await check_user_banned(message.from_user.id):
        return

    text = (
        "🚀 <b>Что нового в WinCleaner v2.0 PRO:</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n\n"
        "✨ <b>Новое ядро очистки реестра 2.0:</b>\n"
        "• Скорость сканирования увеличена на 300% благодаря параллельной обработке веток реестра.\n"
        "• Безопасное удаление битых CLSID, следов деинсталлированных программ и кэша MUI-кэша.\n\n"
        "🛡️ <b>Защита приватности и анти-слежка Windows 11 / 10:</b>\n"
        "• Отключение назойливой рекламы в меню Пуск и Проводнике.\n"
        "• Блокировка телеметрии Microsoft DiagTrack и сборщиков телеметрии Office.\n\n"
        "⚡ <b>Игровой твикер сети (Low Ping):</b>\n"
        "• Оптимизация TCP Window Size, MTU и отключение алгоритма Nagle для минимального инпут-лага.\n"
        "• Быстрый сброс DNS и сетевых очередей в 1 клик.\n\n"
        "🤖 <b>Обновленный Telegram-бот:</b>\n"
        "• Мгновенная оплата через Telegram Stars, баланс и СБП.\n"
        "• Партнерская программа с кэшбэком 15% за приглашенных друзей.\n"
        "• Персональные промокоды и удобное управление устройствами."
    )
    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="💳 Выбрать тариф v2.0 PRO", callback_data="nav_tariffs")],
        [InlineKeyboardButton(text="⚡ Как активировать", callback_data="nav_how_activate")]
    ])
    await message.answer(text, reply_markup=kb, parse_mode="HTML")


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

        stmt = select(Device, License).join(License, Device.license_id == License.id).where(
            License.user_id == user.id, Device.is_active == True
        )
        res = await db.execute(stmt)
        rows = res.all()

        if not rows:
            text = (
                "💻 <b>Привязанные устройства:</b>\n\n"
                "На ваших лицензиях пока нет активных ПК.\n"
                "При первом запуске WinCleaner и вводе ключа ваш компьютер появится здесь автоматически!"
            )
            await message.answer(text, parse_mode="HTML")
            return

        lines = ["💻 <b>Ваши привязанные компьютеры:</b>\n"]
        buttons = []
        for dev, lic in rows:
            safe_dev = escape_html(dev.device_name or "PC")
            safe_os = escape_html(dev.os_version or "Windows 10/11")
            safe_app = escape_html(dev.app_version or "2.0.0")
            last_date = dev.last_seen.strftime('%d.%m.%Y %H:%M')

            lines.append(
                f"🖥️ <b>{safe_dev}</b> (Ключ: <code>{lic.license_key[:9]}...</code>)\n"
                f"   🪟 ОС: <b>{safe_os}</b> | Версия: <code>v{safe_app}</code>\n"
                f"   🆔 HWID: <code>{dev.hwid[:14]}...</code>\n"
                f"   🕒 Был в сети: <code>{last_date} UTC</code>\n"
            )
            buttons.append([
                InlineKeyboardButton(
                    text=f"🔄 Отвязать {safe_dev} (150 ₽)",
                    callback_data=f"buy_tariff:hwid_{dev.id}:150"
                )
            ])

        buttons.append([
            InlineKeyboardButton(text="🔄 Сбросить ВСЕ устройства (150 ₽)", callback_data="buy_tariff:hwid:150")
        ])

        await message.answer("\n".join(lines), reply_markup=InlineKeyboardMarkup(inline_keyboard=buttons), parse_mode="HTML")


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
            "🔄 <b>Перенос лицензии и сброс HWID:</b>\n\n"
            f"Сейчас на ваших лицензиях привязано устройств: <b>{dev_count}</b>.\n\n"
            "💰 <b>Стоимость услуги:</b> <b>150 ₽</b>\n\n"
            "Сброс привязки необходим, если вы:\n"
            "• Переустановили Windows или отформатировали диск\n"
            "• Заменили материнскую плату, процессор или SSD\n"
            "• Хотите перенести программу на другой компьютер\n\n"
            "После подтверждения оплаты 150 ₽ оператор моментально сбросит привязку, "
            "и вы сможете активировать ваш ключ на новом оборудовании!"
        )

        kb = InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="💳 Оплатить сброс HWID (150 ₽)", callback_data="buy_tariff:hwid:150")],
            [InlineKeyboardButton(text="💬 Написать в поддержку", callback_data="user_start_ticket")]
        ])
        await message.answer(text, reply_markup=kb, parse_mode="HTML")


@router.callback_query(F.data.startswith("admin_act_unbind_user:"))
async def cb_admin_act_unbind_user(callback: CallbackQuery):
    if not is_admin(callback.from_user.id, callback.from_user.username):
        await callback.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    target_id = int(callback.data.split(":")[1])
    async with AsyncSessionLocal() as db:
        u_res = await db.execute(select(User).where(User.telegram_id == target_id))
        user = u_res.scalar_one_or_none()
        if not user:
            await callback.answer("Пользователь не найден.")
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

        log = ActivationLog(
            license_id=None,
            hwid="PAID_HWID_RESET_150",
            action="transfer",
            ip_address="telegram_admin_ui",
            success=True,
            details=f"Admin {callback.from_user.id} unbound {unbound_count} devices for user {target_id} (Paid 150 RUB)"
        )
        db.add(log)
        await db.commit()

    bot = get_bot_instance()
    user_confirm = (
        "✅ <b>Привязка HWID успешно сброшена!</b>\n\n"
        "Оплата 150 ₽ подтверждена. Теперь вы можете открыть WinCleaner на новом компьютере "
        "и ввести ваш ключ — он активируется без проблем!"
    )
    user_kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔑 Мои лицензии", callback_data="nav_my_license")]
    ])
    try:
        if bot:
            await bot.send_message(target_id, user_confirm, reply_markup=user_kb, parse_mode="HTML")
            delivery = "✅ Клиент уведомлен в ЛС"
    except Exception as e:
        delivery = f"⚠️ Ошибка отправки в ЛС: {escape_html(str(e))}"

    await callback.answer("HWID сброшен!")
    await callback.message.reply(
        f"✅ <b>HWID сброшен (отвязано {unbound_count} ПК) для клиента <code>{target_id}</code>!</b>\n{delivery}",
        parse_mode="HTML"
    )


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
    callback.data = f"user_req_hwid_key:{key}"
    await cb_user_req_hwid_key(callback)


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
        "🔹 <b>Что если я переустановил Windows или сменил ПК?</b>\n"
        "Перенос лицензии на новый компьютер или после переустановки Windows со сбросом HWID стоит всего 150 ₽. "
        "Нажмите <b>«🔄 Перенести лицензию»</b> в меню бота для оформления заказа.\n\n"
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


@router.message(Command("analytics"))
@router.message(Command("finance"))
@router.callback_query(F.data == "admin_analytics")
async def handle_admin_analytics(event):
    message = event if isinstance(event, Message) else event.message
    admin_id = event.from_user.id
    if not is_admin(admin_id, event.from_user.username):
        if isinstance(event, CallbackQuery):
            await event.answer("⛔ Доступ запрещен.", show_alert=True)
        return

    now = utc_now()
    t_24h = now - timedelta(days=1)
    t_7d = now - timedelta(days=7)
    t_30d = now - timedelta(days=30)

    async with AsyncSessionLocal() as db:
        # Orders stats
        total_revenue = (await db.execute(
            select(func.coalesce(func.sum(Order.amount_rub), 0)).where(Order.status == "paid")
        )).scalar() or 0

        rev_24h = (await db.execute(
            select(func.coalesce(func.sum(Order.amount_rub), 0)).where(Order.status == "paid", Order.created_at >= t_24h)
        )).scalar() or 0

        rev_7d = (await db.execute(
            select(func.coalesce(func.sum(Order.amount_rub), 0)).where(Order.status == "paid", Order.created_at >= t_7d)
        )).scalar() or 0

        rev_30d = (await db.execute(
            select(func.coalesce(func.sum(Order.amount_rub), 0)).where(Order.status == "paid", Order.created_at >= t_30d)
        )).scalar() or 0

        paid_orders_count = (await db.execute(
            select(func.count(Order.id)).where(Order.status == "paid")
        )).scalar() or 0

        total_users = (await db.execute(select(func.count(User.id)))).scalar() or 1
        paying_users = (await db.execute(
            select(func.count(func.distinct(Order.user_id))).where(Order.status == "paid")
        )).scalar() or 0

        cr_pct = round((paying_users / total_users) * 100, 1) if total_users > 0 else 0.0

        # Referrals stats
        total_cashback_paid = (await db.execute(
            select(func.coalesce(func.sum(ReferralHistory.reward_rub), 0))
        )).scalar() or 0

        active_referrers = (await db.execute(
            select(func.count(func.distinct(ReferralHistory.referrer_id)))
        )).scalar() or 0

        # Promo stats
        promos_count = (await db.execute(select(func.count(PromoCode.id)))).scalar() or 0

    analytics_text = (
        "📊 <b>ФИНАНСОВАЯ И БИЗНЕС-АНАЛИТИКА WinCleaner</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"💰 <b>Общая выручка:</b> <b>{total_revenue:,} ₽</b>\n"
        f"📅 <b>За 24 часа:</b> <b>{rev_24h:,} ₽</b>\n"
        f"📅 <b>За 7 дней:</b> <b>{rev_7d:,} ₽</b>\n"
        f"📅 <b>За 30 дней:</b> <b>{rev_30d:,} ₽</b>\n"
        f"📦 <b>Оплаченных заказов:</b> <b>{paid_orders_count}</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        f"👥 <b>Конверсия в оплату (CR):</b> <b>{cr_pct}%</b> ({paying_users} из {total_users} клиентов)\n"
        f"🤝 <b>Реферальный кэшбэк:</b> начислено <b>{total_cashback_paid:,} ₽</b> ({active_referrers} партнеров)\n"
        f"🎟 <b>Промокодов в базе:</b> <b>{promos_count}</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "⚡ <i>Авто-оплаты через Telegram Stars и баланс зачисляются моментально.</i>"
    )

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔄 Обновить аналитику", callback_data="admin_analytics")],
        [InlineKeyboardButton(text="🎟 Список промокодов", callback_data="admin_promos_list")],
        [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
    ])

    if isinstance(event, CallbackQuery):
        try:
            await event.message.edit_text(analytics_text, reply_markup=kb, parse_mode="HTML")
        except Exception:
            await event.message.answer(analytics_text, reply_markup=kb, parse_mode="HTML")
        await event.answer()
    else:
        await message.answer(analytics_text, reply_markup=kb, parse_mode="HTML")


@router.message(Command("createpromo"))
async def cmd_create_promo(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    parts = message.text.split()
    if len(parts) < 3 or not parts[2].isdigit():
        await message.answer(
            "Использование: <code>/createpromo &lt;КОД&gt; &lt;СКИДКА%&gt; [ЛИМИТ]</code>\n"
            "Пример: <code>/createpromo START20 20 100</code> (скидка 20%, 100 активаций)",
            parse_mode="HTML"
        )
        return

    code = parts[1].strip().upper()
    discount = int(parts[2])
    max_uses = int(parts[3]) if len(parts) > 3 and parts[3].isdigit() else 100

    async with AsyncSessionLocal() as db:
        existing = (await db.execute(select(PromoCode).where(PromoCode.code == code))).scalar_one_or_none()
        if existing:
            existing.discount_percent = discount
            existing.max_uses = max_uses
            existing.is_active = True
        else:
            p = PromoCode(
                code=code,
                discount_percent=discount,
                max_uses=max_uses,
                current_uses=0,
                is_active=True
            )
            db.add(p)
        await db.commit()

    await message.answer(
        f"✅ <b>Промокод «{code}» успешно сохранен!</b>\n"
        f"• Скидка: <b>{discount}%</b>\n"
        f"• Лимит использований: <b>{max_uses}</b>",
        parse_mode="HTML"
    )


@router.message(Command("promos"))
@router.callback_query(F.data == "admin_promos_list")
async def handle_admin_promos_list(event):
    message = event if isinstance(event, Message) else event.message
    admin_id = event.from_user.id
    if not is_admin(admin_id, event.from_user.username):
        return

    async with AsyncSessionLocal() as db:
        res = await db.execute(select(PromoCode).order_by(desc(PromoCode.id)))
        promos = res.scalars().all()

    if not promos:
        txt = "🎟 В системе пока нет промокодов.\nСоздайте новый: <code>/createpromo START20 20 100</code>"
    else:
        lines = ["🎟 <b>Активные промокоды WinCleaner:</b>\n"]
        for p in promos:
            status = "🟢" if p.is_active else "🔴"
            lines.append(f"• {status} <code>{p.code}</code> — Скидка: <b>{p.discount_percent}%</b> (Исп: {p.current_uses}/{p.max_uses})")
        txt = "\n".join(lines)

    kb = InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="🔙 В админ-панель", callback_data="admin_panel_refresh")]
    ])

    if isinstance(event, CallbackQuery):
        try:
            await event.message.edit_text(txt, reply_markup=kb, parse_mode="HTML")
        except Exception:
            await event.message.answer(txt, reply_markup=kb, parse_mode="HTML")
        await event.answer()
    else:
        await message.answer(txt, reply_markup=kb, parse_mode="HTML")


@router.message(Command("announce"))
async def cmd_announce(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    text_to_send = message.text.partition(" ")[2].strip()
    if not text_to_send:
        await message.answer(
            "Использование: <code>/announce &lt;текст анонса обновления&gt;</code>\n"
            "Пример: <code>/announce 🚀 Вышло глобальное обновление WinCleaner v2.0 PRO! Новые твики и очистка уже доступны.</code>",
            parse_mode="HTML"
        )
        return

    formatted = (
        "📢 <b>ОФИЦИАЛЬНОЕ ОБНОВЛЕНИЕ WinCleaner</b>\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n\n"
        f"{escape_html(text_to_send)}\n\n"
        "━━━━━━━━━━━━━━━━━━━━━━\n"
        "<i>Подробности в меню «📢 Что нового» и «💳 Купить лицензию»</i>"
    )

    bot = get_bot_instance()
    async with AsyncSessionLocal() as db:
        stmt = select(User.telegram_id).distinct()
        res = await db.execute(stmt)
        user_ids = res.scalars().all()

    status_msg = await message.answer(f"📢 Запуск рассылки анонса для {len(user_ids)} пользователей...")
    success = 0
    failed = 0
    for uid in user_ids:
        try:
            await bot.send_message(uid, formatted, parse_mode="HTML")
            success += 1
            await asyncio.sleep(0.05)
        except Exception:
            failed += 1

    await status_msg.edit_text(
        f"📢 <b>Анонс обновления успешно разослан!</b>\n\n"
        f"✅ Доставлено: <b>{success}</b>\n"
        f"❌ Ошибок: <b>{failed}</b>",
        parse_mode="HTML"
    )


@router.message(Command("check_expirations"))
async def cmd_check_expirations(message: Message):
    if not is_admin(message.from_user.id, message.from_user.username):
        return

    from app.retention import check_and_send_license_reminders
    bot = get_bot_instance()
    stats = await check_and_send_license_reminders(bot)

    await message.answer(
        "🔄 <b>Проверка истекающих лицензий завершена:</b>\n\n"
        f"• Напоминаний за 3 дня: <b>{stats.get('reminded_3d', 0)}</b>\n"
        f"• Напоминаний за 1 день: <b>{stats.get('reminded_1d', 0)}</b>\n"
        f"• Уведомлений об окончании: <b>{stats.get('reminded_expired', 0)}</b>",
        parse_mode="HTML"
    )


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

    # Check if user was proposing a feature idea
    if sender_id in _user_waiting_idea:
        raw_text = (message.text or message.caption or "").strip()
        menu_triggers = {
            "🔑 Моя лицензия", "💳 Купить лицензию", "💻 Мои устройства", "🔄 Перенести лицензию",
            "🤝 Партнерка", "🎟 Промокод", "💡 Голосование", "📢 Что нового", "❓ FAQ & Помощь",
            "💬 Поддержка", "⚡ Как активировать", "🛡️ Админ-панель"
        }
        if raw_text.lower() in {"/cancel", "отмена", "❌ отмена"}:
            _user_waiting_idea.discard(sender_id)
            await message.answer("❌ <b>Предложение идеи отменено.</b>", reply_markup=get_main_keyboard(is_admin(sender_id)), parse_mode="HTML")
            return
        elif raw_text in menu_triggers:
            _user_waiting_idea.discard(sender_id)
        else:
            _user_waiting_idea.discard(sender_id)
            if len(raw_text) < 5:
                await message.answer(
                    "⚠️ <b>Слишком короткое описание.</b>\n"
                    "Пожалуйста, опишите идею подробнее (минимум 5 символов), чтобы сообщество могло ее оценить.",
                    reply_markup=get_main_keyboard(is_admin(sender_id)),
                    parse_mode="HTML"
                )
                return

            # Extract title and description
            lines = [l.strip() for l in raw_text.splitlines() if l.strip()]
            first_line = lines[0] if lines else raw_text
            title = first_line[:100]
            description = raw_text
            author_name = message.from_user.full_name or message.from_user.username or "Пользователь"

            async with AsyncSessionLocal() as db:
                u_res = await db.execute(select(User).where(User.telegram_id == sender_id))
                db_user = u_res.scalar_one_or_none()
                user_pk = db_user.id if db_user else None

                idea = FeatureIdea(
                    user_id=user_pk,
                    user_telegram_id=sender_id,
                    author_name=author_name,
                    title=title,
                    description=description,
                    votes_count=1,
                    status="approved"
                )
                db.add(idea)
                await db.flush()

                init_vote = FeatureVote(
                    idea_id=idea.id,
                    user_telegram_id=sender_id
                )
                db.add(init_vote)
                await db.commit()
                idea_id = idea.id

            kb = InlineKeyboardMarkup(inline_keyboard=[
                [InlineKeyboardButton(text="🔥 Смотреть идеи и голосовать", callback_data="ideas_browse:0")],
                [InlineKeyboardButton(text="💡 В меню идей", callback_data="ideas_hub")]
            ])
            await message.answer(
                f"🎉 <b>Ваша идея успешно опубликована!</b>\n"
                f"━━━━━━━━━━━━━━━━━━━━━━\n"
                f"💡 <b>«{escape_html(title)}»</b>\n\n"
                f"Ваш голос уже автоматически учтен (+1 👍).\n"
                f"Теперь другие пользователи могут голосовать за ваше предложение в разделе <b>💡 Голосование</b>!",
                reply_markup=kb,
                parse_mode="HTML"
            )

            # Alert admin with 1-click moderation buttons
            adm_kb = InlineKeyboardMarkup(inline_keyboard=[
                [
                    InlineKeyboardButton(text="🛠️ В разработку", callback_data=f"adm_idea_st:{idea_id}:in_progress"),
                    InlineKeyboardButton(text="✅ Реализовано", callback_data=f"adm_idea_st:{idea_id}:completed")
                ],
                [
                    InlineKeyboardButton(text="❌ Отклонить", callback_data=f"adm_idea_st:{idea_id}:rejected"),
                    InlineKeyboardButton(text="🗑️ Удалить", callback_data=f"adm_idea_del:{idea_id}")
                ]
            ])
            await send_admin_alert(
                f"💡 <b>НОВАЯ ИДЕЯ ОТ ПОЛЬЗОВАТЕЛЯ!</b>\n"
                f"━━━━━━━━━━━━━━━━━━━━━━\n"
                f"👤 <b>Автор:</b> {escape_html(author_name)} (<code>{sender_id}</code>)\n"
                f"🆔 <b>ID идеи:</b> <code>{idea_id}</code>\n"
                f"📌 <b>Заголовок:</b> <b>{escape_html(title)}</b>\n\n"
                f"📝 <b>Описание:</b>\n{escape_html(description)}",
                reply_markup=adm_kb
            )
            return

    # Check if user was entering a promo code
    if sender_id in _user_waiting_promo:
        _user_waiting_promo.discard(sender_id)
        raw_code = (message.text or "").strip().upper()
        async with AsyncSessionLocal() as db:
            p_stmt = select(PromoCode).where(PromoCode.code == raw_code, PromoCode.is_active == True)
            p_res = await db.execute(p_stmt)
            promo = p_res.scalar_one_or_none()

            if not promo:
                await message.answer(
                    f"❌ <b>Промокод «{escape_html(raw_code)}» не найден или недействителен.</b>\n\n"
                    "Проверьте правильность написания или напишите в поддержку.",
                    reply_markup=get_tariffs_inline_keyboard(0),
                    parse_mode="HTML"
                )
                return

            if promo.max_uses and promo.current_uses >= promo.max_uses:
                await message.answer(
                    f"❌ <b>Лимит активаций промокода «{escape_html(raw_code)}» исчерпан.</b>",
                    reply_markup=get_tariffs_inline_keyboard(0),
                    parse_mode="HTML"
                )
                return

            # Activate promo for user
            promo.current_uses += 1
            _user_active_promo[sender_id] = {
                "code": promo.code,
                "discount_percent": promo.discount_percent
            }
            await db.commit()

        await message.answer(
            f"🎉 <b>Промокод «{promo.code}» успешно активирован!</b>\n\n"
            f"Вам начислена скидка <b>{promo.discount_percent}%</b> на все тарифы WinCleaner!\n"
            "Цены в каталоге ниже пересчитаны со скидкой ⬇️",
            reply_markup=get_tariffs_inline_keyboard(promo.discount_percent),
            parse_mode="HTML"
        )
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
