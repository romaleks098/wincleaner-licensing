import os
import asyncio
import logging
import urllib.request
from contextlib import asynccontextmanager

import uvicorn
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from aiogram import Bot, Dispatcher

from app.config import settings
from app.database import init_db
from app.api import router as api_router
from app.bot import router as bot_router, set_bot_instance, set_dp_instance

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s"
)
logger = logging.getLogger("server")

bot_task = None
keep_alive_task = None
bot_instance = None
dp_instance = None

def _sync_ping(url: str) -> int:
    req = urllib.request.Request(
        url,
        headers={"User-Agent": "WinCleaner-KeepAlive/2.0"}
    )
    with urllib.request.urlopen(req, timeout=15) as resp:
        return resp.status

async def run_keep_alive():
    """
    Prevents Render Free Tier from spinning down (which happens after 15 min of no incoming requests).
    Periodically sends external HTTP GET requests to reset the inactivity timer.
    """
    external_url = os.environ.get("RENDER_EXTERNAL_URL") or "https://wincleaner-license.onrender.com"
    ping_url = f"{external_url.rstrip('/')}/api/v1/health"

    # Only activate keep-alive if running on Render or in production
    is_render = bool(os.environ.get("RENDER") or os.environ.get("RENDER_EXTERNAL_URL") or settings.ENVIRONMENT == "production")
    if not is_render:
        logger.info("Local development mode: internal keep-alive loop is inactive.")
        return

    logger.info(f"Keep-alive loop initiated for target: {ping_url}")
    await asyncio.sleep(30)

    while True:
        try:
            status_code = await asyncio.to_thread(_sync_ping, ping_url)
            logger.info(f"Keep-alive self-ping sent to {ping_url} — HTTP {status_code}")
        except asyncio.CancelledError:
            break
        except Exception as e:
            logger.warning(f"Keep-alive self-ping warning: {e}")

        # Render free tier goes to sleep after 15 minutes. Ping every 5 minutes (300s) ensures it stays awake.
        try:
            await asyncio.sleep(300)
        except asyncio.CancelledError:
            break

async def start_bot_service():
    """
    Runs Aiogram 3 bot.
    On Render (production): registers Telegram Webhook at /api/v1/telegram-webhook.
    Whenever any user writes in Telegram, Telegram pushes an incoming HTTPS request
    directly to Render, which automatically wakes Render up and prevents freezing!
    Locally: runs standard long-polling loop with auto-reconnection.
    """
    if not (settings.BOT_TOKEN and len(settings.BOT_TOKEN) > 10):
        logger.warning("BOT_TOKEN is not configured. Telegram bot disabled.")
        return

    global bot_instance, dp_instance
    bot_instance = Bot(token=settings.BOT_TOKEN)
    dp_instance = Dispatcher()
    dp_instance.include_router(bot_router)

    set_bot_instance(bot_instance)
    set_dp_instance(dp_instance)

    # Detect if we should use Webhook or Polling
    is_render = bool(os.environ.get("RENDER") or os.environ.get("RENDER_EXTERNAL_URL") or os.environ.get("USE_WEBHOOK"))
    external_url = os.environ.get("RENDER_EXTERNAL_URL") or "https://wincleaner-license.onrender.com"

    if is_render and "onrender.com" in external_url:
        webhook_url = f"{external_url.rstrip('/')}/api/v1/telegram-webhook"
        logger.info(f"Render detected! Registering Telegram Webhook: {webhook_url}")
        try:
            # Set webhook with drop_pending_updates=False so no messages are lost
            await bot_instance.set_webhook(
                url=webhook_url,
                drop_pending_updates=False,
                allowed_updates=["message", "callback_query", "pre_checkout_query"]
            )
            logger.info("Telegram Webhook registered successfully! Bot is active 24/7 via incoming HTTPS requests.")
            from app.retention import start_retention_scheduler
            asyncio.create_task(start_retention_scheduler(bot_instance))
            return
        except Exception as e:
            logger.error(f"Failed to set Telegram Webhook: {e}. Falling back to polling...")

    # Local mode: polling with auto-reconnect
    logger.info("Running in long-polling mode (local development)...")
    from app.retention import start_retention_scheduler
    asyncio.create_task(start_retention_scheduler(bot_instance))
    retry_delay = 5
    while True:
        try:
            await bot_instance.delete_webhook(drop_pending_updates=False)
            await dp_instance.start_polling(bot_instance, handle_signals=False)
            logger.info("Bot polling stopped cleanly.")
            break
        except asyncio.CancelledError:
            logger.info("Bot polling cancelled.")
            break
        except Exception as e:
            logger.error(f"Telegram polling error: {e}. Reconnecting in {retry_delay}s...")
            try:
                await asyncio.sleep(retry_delay)
            except asyncio.CancelledError:
                break
            retry_delay = min(retry_delay * 2, 60)

@asynccontextmanager
async def lifespan(app: FastAPI):
    global bot_task, keep_alive_task, bot_instance
    logger.info("FastAPI lifespan starting — launching background tasks...")

    async def background_startup():
        # Initialize DB in background without blocking HTTP healthcheck
        try:
            await init_db()
        except Exception as e:
            logger.error(f"Database startup error: {e}")

        # Start Bot service (Webhook on Render or Polling locally)
        await start_bot_service()

    bot_task = asyncio.create_task(background_startup())
    keep_alive_task = asyncio.create_task(run_keep_alive())

    # Yield IMMEDIATELY so uvicorn binds to PORT and passes Render healthcheck in 1s
    yield

    logger.info("Shutting down background tasks...")
    if keep_alive_task:
        keep_alive_task.cancel()
        try:
            await keep_alive_task
        except asyncio.CancelledError:
            pass

    if bot_task:
        bot_task.cancel()
        try:
            await bot_task
        except asyncio.CancelledError:
            pass

    if bot_instance:
        try:
            await bot_instance.session.close()
        except Exception:
            pass
    logger.info("Server shutdown complete.")

app = FastAPI(
    title="WinCleaner Licensing API",
    version="2.0.0",
    lifespan=lifespan
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

@app.get("/")
async def root():
    return {"status": "ok", "service": "wincleaner-licensing"}

app.include_router(api_router)

if __name__ == "__main__":
    port = int(os.environ.get("PORT", "10000"))
    logger.info(f"Starting server on 0.0.0.0:{port}...")
    uvicorn.run(
        "main:app",
        host="0.0.0.0",
        port=port,
        reload=False
    )
