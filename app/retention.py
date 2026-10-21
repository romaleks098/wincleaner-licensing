import asyncio
import logging
from datetime import datetime, timedelta, timezone
from sqlalchemy import select, update
from aiogram.types import InlineKeyboardMarkup, InlineKeyboardButton

from app.database import AsyncSessionLocal
from app.models import License, User

logger = logging.getLogger("retention")

def utc_now() -> datetime:
    return datetime.now(timezone.utc).replace(tzinfo=None)

async def check_and_send_license_reminders(bot) -> dict:
    """
    Scans active licenses and sends automated retention notifications:
    - 3 days before expiration
    - 1 day before expiration
    - On expiration day
    """
    if not bot:
        return {"status": "no_bot"}

    now = utc_now()
    stats = {"reminded_3d": 0, "reminded_1d": 0, "reminded_expired": 0}

    async with AsyncSessionLocal() as db:
        # 1. Check 3 days before expiration (between 2 and 3.5 days remaining)
        t_3d_max = now + timedelta(days=3, hours=12)
        t_3d_min = now + timedelta(days=2)

        stmt_3d = select(License, User).join(User, License.user_id == User.id).where(
            License.status == "active",
            License.expires_at >= t_3d_min,
            License.expires_at <= t_3d_max,
            License.reminder_3d_sent == False
        )
        res_3d = await db.execute(stmt_3d)
        rows_3d = res_3d.all()

        for lic, user in rows_3d:
            try:
                days_left = max(1, (lic.expires_at - now).days)
                exp_date = lic.expires_at.strftime("%d.%m.%Y")
                msg = (
                    "⏳ <b>Напоминание: срок действия вашей лицензии истекает!</b>\n"
                    "━━━━━━━━━━━━━━━━━━━━━━\n"
                    f"🔑 Ключ: <code>{lic.license_key}</code>\n"
                    f"📅 Окончание: <b>{exp_date}</b> (осталось ~{days_left} дн.)\n"
                    "━━━━━━━━━━━━━━━━━━━━━━\n\n"
                    "Продлите доступ прямо сейчас, чтобы ваш ПК продолжал работать на максимальной скорости "
                    "без сбоев и системного мусора.\n\n"
                    "🎁 <i>Используйте промокод</i> <code>RENEW10</code> <i>для скидки 10% на продление!</i>"
                )
                kb = InlineKeyboardMarkup(inline_keyboard=[
                    [InlineKeyboardButton(text="💳 Продлить лицензию", callback_data="nav_tariffs")],
                    [InlineKeyboardButton(text="🎟 Ввести промокод", callback_data="user_enter_promo")]
                ])
                await bot.send_message(user.telegram_id, msg, reply_markup=kb, parse_mode="HTML")
                lic.reminder_3d_sent = True
                stats["reminded_3d"] += 1
            except Exception as e:
                logger.warning(f"Failed to send 3d reminder to {user.telegram_id}: {e}")

        # 2. Check 1 day before expiration (between 4 hours and 28 hours remaining)
        t_1d_max = now + timedelta(hours=28)
        t_1d_min = now + timedelta(hours=4)

        stmt_1d = select(License, User).join(User, License.user_id == User.id).where(
            License.status == "active",
            License.expires_at >= t_1d_min,
            License.expires_at <= t_1d_max,
            License.reminder_1d_sent == False
        )
        res_1d = await db.execute(stmt_1d)
        rows_1d = res_1d.all()

        for lic, user in rows_1d:
            try:
                exp_date = lic.expires_at.strftime("%d.%m.%Y %H:%M")
                msg = (
                    "⚠️ <b>Внимание: завтра заканчивается лицензия WinCleaner!</b>\n"
                    "━━━━━━━━━━━━━━━━━━━━━━\n"
                    f"🔑 Ключ: <code>{lic.license_key}</code>\n"
                    f"⏳ Действует до: <b>{exp_date} UTC</b>\n"
                    "━━━━━━━━━━━━━━━━━━━━━━\n\n"
                    "После окончания ключ перестанет валидироваться в приложении. "
                    "Продлите подписку в 1 клик, чтобы не прерывать оптимизацию!"
                )
                kb = InlineKeyboardMarkup(inline_keyboard=[
                    [InlineKeyboardButton(text="⚡ Продлить сейчас", callback_data="nav_tariffs")]
                ])
                await bot.send_message(user.telegram_id, msg, reply_markup=kb, parse_mode="HTML")
                lic.reminder_1d_sent = True
                stats["reminded_1d"] += 1
            except Exception as e:
                logger.warning(f"Failed to send 1d reminder to {user.telegram_id}: {e}")

        # 3. Check Expired licenses
        stmt_exp = select(License, User).join(User, License.user_id == User.id).where(
            License.expires_at < now,
            License.status == "active",
            License.expired_sent == False
        )
        res_exp = await db.execute(stmt_exp)
        rows_exp = res_exp.all()

        for lic, user in rows_exp:
            try:
                lic.status = "expired"
                lic.expired_sent = True
                msg = (
                    "🔴 <b>Срок действия вашей лицензии WinCleaner истек.</b>\n\n"
                    f"🔑 Ключ: <code>{lic.license_key}</code>\n\n"
                    "Спасибо, что пользуетесь WinCleaner! Вы можете в любой момент возобновить подписку "
                    "или приобрести бессрочную лицензию Lifetime."
                )
                kb = InlineKeyboardMarkup(inline_keyboard=[
                    [InlineKeyboardButton(text="💳 Выбрать тариф и продлить", callback_data="nav_tariffs")]
                ])
                await bot.send_message(user.telegram_id, msg, reply_markup=kb, parse_mode="HTML")
                stats["reminded_expired"] += 1
            except Exception as e:
                logger.warning(f"Failed to send expired notice to {user.telegram_id}: {e}")

        await db.commit()

    return stats

async def start_retention_scheduler(bot):
    """
    Background worker running periodically (every 1 hour).
    """
    logger.info("Retention reminder scheduler started.")
    while True:
        try:
            await check_and_send_license_reminders(bot)
        except Exception as e:
            logger.error(f"Error in retention scheduler cycle: {e}")
        # Wait 1 hour before next scan
        await asyncio.sleep(3600)
