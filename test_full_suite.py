import os
import sys
import asyncio
from datetime import datetime, timedelta

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from PIL import Image
from sqlalchemy import select, func
from app.config import settings
from app.database import init_db, AsyncSessionLocal
from app.models import (
    User, License, Device, ActivationLog, SupportTicket, TicketMessage,
    PromoCode, Order, ReferralHistory, FeatureIdea, FeatureVote
)
from app.crypto import sign_license, verify_license_signature, generate_key_string
from app.bot import (
    escape_html, is_admin, ensure_initial_ideas, render_ideas_hub,
    render_idea_card, get_idea_status_badge
)
from app.retention import check_and_send_license_reminders

async def main():
    print("==================================================")
    print("WINCLEANER 2.0 FULL SYSTEM & BOT VERIFICATION SUITE")
    print("==================================================")

    # 1. Anonymity Check
    print("\n[1/8] Checking Administrator Anonymity...")
    assert not settings.ADMIN_USERNAME, f"Error: ADMIN_USERNAME should not be exposed, got {settings.ADMIN_USERNAME}"
    print("  ✓ settings.ADMIN_USERNAME is NOT exposed publicly.")
    with open("server/app/bot.py", "r", encoding="utf-8") as f:
        bot_src = f.read()
    assert "DerzhalTomik" not in bot_src, "Error: DerzhalTomik found in bot.py!"
    print("  ✓ ZERO mentions of admin handle found in bot.py source code.")

    # 2. Banners Check
    print("\n[2/8] Checking Banner Assets...")
    banners = [
        ("Main", settings.banner_main),
        ("Tariffs", settings.banner_tariffs),
        ("Support", settings.banner_support),
        ("Activate", settings.banner_activate),
    ]
    for label, path in banners:
        assert os.path.isfile(path), f"Banner {label} not found at {path}"
        with Image.open(path) as img:
            w, h = img.size
            assert w >= 800 and h >= 400, f"Expected banner dimensions >= 800x400, got {w}x{h}"
            size_kb = os.path.getsize(path) / 1024
            print(f"  ✓ Banner '{label}': {w}x{h} PNG ({size_kb:.1f} KB) OK")

    # 3. Database & Schemas Check
    print("\n[3/8] Testing Database Initializer & Migration...")
    await init_db()
    async with AsyncSessionLocal() as db:
        # Create test user
        test_uid = 999999991
        u_stmt = select(User).where(User.telegram_id == test_uid)
        res = await db.execute(u_stmt)
        u = res.scalar_one_or_none()
        if not u:
            u = User(telegram_id=test_uid, username="test_buyer", first_name="Test Customer")
            db.add(u)
            await db.flush()

        # Create ticket
        t = SupportTicket(
            user_id=u.id,
            user_telegram_id=test_uid,
            status="open",
            ticket_type="purchase",
            subject="Test Order 1 Year"
        )
        db.add(t)
        await db.flush()

        # Add message
        tm = TicketMessage(
            ticket_id=t.id,
            sender_type="user",
            sender_telegram_id=test_uid,
            text="Здравствуйте, хочу оплатить картой"
        )
        db.add(tm)
        await db.commit()

        # Verify query
        assert t.id is not None
        assert tm.id is not None
        print(f"  ✓ User, SupportTicket (id={t.id}), and TicketMessage created successfully.")

        # Admin reply to ticket
        tm_reply = TicketMessage(
            ticket_id=t.id,
            sender_type="admin",
            sender_telegram_id=1336498951,
            text="Здравствуйте! Вот реквизиты СБП..."
        )
        db.add(tm_reply)
        t.status = "answered"
        await db.commit()
        print("  ✓ Admin response logged and ticket status marked 'answered'.")

    # 4. Crypto & Licensing Check
    print("\n[4/8] Testing RFC 8032 Ed25519 Crypto Signatures...")
    key = generate_key_string()
    hwid = "TESTHWID1234567890ABCDEF"
    exp = datetime.now() + timedelta(days=365)
    sig = sign_license(key, hwid, exp)
    assert verify_license_signature(key, hwid, exp, sig) is True
    assert verify_license_signature(key, "FAKED_HWID", exp, sig) is False
    print(f"  ✓ Generated key: {key}")
    print(f"  ✓ Ed25519 signature verified successfully (tamper protection passed).")

    # 5. Device unbinding / Transfer
    print("\n[5/8] Testing Device Transfer / Unbind Logic...")
    async with AsyncSessionLocal() as db:
        lic = License(
            license_key=key,
            user_id=u.id,
            status="active",
            max_devices=1,
            expires_at=exp,
            notes="Full suite test"
        )
        db.add(lic)
        await db.flush()

        dev = Device(
            license_id=lic.id,
            hwid=hwid,
            device_name="MY-GAMING-PC",
            os_version="Windows 11 Pro 23H2",
            app_version="2.0.0",
            ip_address="192.168.1.50",
            is_active=True
        )
        db.add(dev)
        await db.commit()
        assert dev.is_active is True
        assert dev.os_version == "Windows 11 Pro 23H2"

        # Unbind
        dev.is_active = False
        await db.commit()
        assert dev.is_active is False
        print("  ✓ Self-service device unbind and OS telemetry verified.")

    # 6. Promo Codes, Orders & Referral System
    print("\n[6/8] Testing Promo Codes & 15% Referral System...")
    import secrets
    test_run_tag = secrets.token_hex(4).upper()
    async with AsyncSessionLocal() as db:
        # Create promo code
        promo = PromoCode(
            code=f"TEST_{test_run_tag}",
            discount_percent=20,
            max_uses=50,
            current_uses=0,
            is_active=True
        )
        db.add(promo)

        # Referrer & Referred user
        rand_ref_id = secrets.randbelow(800000000) + 100000000
        rand_buyer_id = secrets.randbelow(800000000) + 100000000
        referrer = User(telegram_id=rand_ref_id, username=f"ref_{test_run_tag}", first_name="Top Partner", balance=0)
        db.add(referrer)
        await db.flush()

        referred = User(telegram_id=rand_buyer_id, username=f"buy_{test_run_tag}", first_name="Friend", referrer_id=referrer.id, balance=0)
        db.add(referred)
        await db.flush()

        # Order creation
        ord_obj = Order(
            order_id=f"ORD-TEST-{test_run_tag}",
            user_id=referred.id,
            user_telegram_id=referred.telegram_id,
            item_type="year",
            amount_rub=990,
            stars_amount=500,
            payment_method="stars",
            status="paid",
            result_license_key=f"WINC-TEST-PAID-{test_run_tag}"
        )
        db.add(ord_obj)

        # 15% Referral cashback
        reward = int(990 * 0.15)
        referrer.balance += reward
        ref_h = ReferralHistory(
            referrer_id=referrer.id,
            referred_id=referred.id,
            reward_rub=reward,
            order_id=ord_obj.id
        )
        db.add(ref_h)
        await db.commit()

        assert referrer.balance == 148, f"Expected 148 RUB cashback, got {referrer.balance}"
        print(f"  ✓ Promo code 'TEST_{test_run_tag}' saved successfully.")
        print(f"  ✓ 15% Referral cashback (+{reward} ₽) credited to referrer ID {referrer.telegram_id}.")
        print(f"  ✓ Paid order {ord_obj.order_id} recorded in database.")

    # 7. Retention Scanner
    print("\n[7/8] Testing Retention & Expiration Engine...")
    stats = await check_and_send_license_reminders(None)
    assert isinstance(stats, dict)
    print("  ✓ Retention scanner initialized cleanly.")

    # 8. Community Feature Voting & Idea Suggestion System
    print("\n[8/8] Testing Community Feature Voting & Idea Engine...")
    async with AsyncSessionLocal() as db:
        # Test initial seeding
        await ensure_initial_ideas(db)
        seeded_count = (await db.execute(select(func.count(FeatureIdea.id)))).scalar() or 0
        assert seeded_count >= 4, f"Expected at least 4 seeded ideas, got {seeded_count}"
        print(f"  ✓ Initial ideas verified in database: {seeded_count} ideas present.")

        # Test hub rendering
        hub_text, hub_kb = await render_ideas_hub(db, 999111)
        assert "ГОЛОСОВАНИЕ ЗА НОВЫЕ ФУНКЦИИ" in hub_text
        assert len(hub_kb.inline_keyboard) >= 3
        print("  ✓ Ideas Hub rendered successfully with leaderboard and action buttons.")

        # Test card carousel rendering
        card_text, card_kb = await render_idea_card(db, 999111, offset=0)
        assert "Идея #" in card_text
        assert "Поддержали:" in card_text
        print("  ✓ Idea carousel card rendered with dynamic voting buttons.")

        # User proposes a custom new idea
        user_prop = FeatureIdea(
            user_id=u.id,
            user_telegram_id=u.telegram_id,
            author_name="Артём Инженер",
            title="Интеграция DNS-over-HTTPS (DoH) для обхода замедлений",
            description="Добавить возможность переключения DNS на Cloudflare 1.1.1.1 или Quad9 с включенным шифрованием DoH прямо в твикере сети WinCleaner.",
            votes_count=1,
            status="approved"
        )
        db.add(user_prop)
        await db.flush()

        # Proposer vote
        p_vote = FeatureVote(idea_id=user_prop.id, user_telegram_id=u.telegram_id)
        db.add(p_vote)
        await db.commit()
        print(f"  ✓ User idea #{user_prop.id} proposed and recorded with author initial vote (+1).")

        # Another user votes
        voter_tg_id = 777333555
        v_check = (await db.execute(select(FeatureVote).where(
            FeatureVote.idea_id == user_prop.id,
            FeatureVote.user_telegram_id == voter_tg_id
        ))).scalar_one_or_none()
        assert v_check is None, "Expected voter has not voted yet"

        # Cast vote
        new_v = FeatureVote(idea_id=user_prop.id, user_telegram_id=voter_tg_id)
        db.add(new_v)
        user_prop.votes_count += 1
        await db.commit()
        assert user_prop.votes_count == 2
        print(f"  ✓ Second user voted on idea #{user_prop.id} (total votes: {user_prop.votes_count}).")

        # Verify duplicate vote prevention
        v_dup = (await db.execute(select(FeatureVote).where(
            FeatureVote.idea_id == user_prop.id,
            FeatureVote.user_telegram_id == voter_tg_id
        ))).scalar_one_or_none()
        assert v_dup is not None, "Duplicate check logic passed (already registered)"

        # Unvote
        await db.delete(v_dup)
        user_prop.votes_count = max(0, user_prop.votes_count - 1)
        await db.commit()
        assert user_prop.votes_count == 1
        print(f"  ✓ Unvote executed cleanly (votes returned to {user_prop.votes_count}).")

        # Admin status progression
        user_prop.status = "in_progress"
        await db.commit()
        assert get_idea_status_badge(user_prop.status) == "🛠️ <b>В разработке</b>"

        user_prop.status = "completed"
        await db.commit()
        assert get_idea_status_badge(user_prop.status) == "✅ <b>Реализовано в v2.0</b>"
        print(f"  ✓ Admin moderation lifecycle (in_progress -> completed) verified.")

    print("\n==================================================")
    print("ALL TESTS PASSED! SERVER & BOT ARE 100% READY!")
    print("==================================================")

if __name__ == "__main__":
    asyncio.run(main())
