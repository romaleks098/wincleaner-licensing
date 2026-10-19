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
from app.bot import router as bot_router

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s"
)
logger = logging.getLogger("server")

bot_task = None
keep_alive_task = None
bot_instance = None

def _sync_ping(url: str) -> int:
    req = urllib.request.Request(
        url,
        headers={"User-Agent": "WinCleaner-KeepAlive/2.0"}
    )
    with urllib.request.urlopen(req, timeout=15) as resp:
        return resp.status

async def run_keep_alive():
    """
    Prevents Render Free Tier from idling/spinning down (which happens after 15 min of no incoming requests).
    Periodically sends external HTTP GET requests to reset the inactivity timer.
    """
    external_url = os.environ.get("RENDER_EXTERNAL_URL") or "https://wincleaner-license.onrender.com"
    ping_url = f"{external_url.rstrip('/')}/api/v1/health"

    # Only activate keep-alive if running on Render or in production
    is_render = bool(os.environ.get("RENDER") or os.environ.get("RENDER_EXTERNAL_URL"))
    if not is_render and settings.ENVIRONMENT != "production":
        logger.info("Local development mode: internal keep-alive loop is inactive.")
        return

    logger.info(f"Keep-alive loop initiated for target: {ping_url}")
    # Wait 45 seconds after initial launch before first ping
    await asyncio.sleep(45)

    while True:
        try:
            status_code = await asyncio.to_thread(_sync_ping, ping_url)
            logger.info(f"Keep-alive self-ping sent to {ping_url} — HTTP {status_code}")
        except asyncio.CancelledError:
            break
        except Exception as e:
            logger.warning(f"Keep-alive self-ping warning: {e}")

        # Render free tier goes to sleep after 15 minutes. Ping every 8 minutes (480s) ensures it never sleeps.
        try:
            await asyncio.sleep(480)
        except asyncio.CancelledError:
            break

async def run_bot_polling():
    """
    Runs Aiogram 3 bot polling with auto-reconnection loop in case of temporary network glitches.
    """
    if not (settings.BOT_TOKEN and len(settings.BOT_TOKEN) > 10):
        logger.warning("BOT_TOKEN is not configured. Telegram bot disabled.")
        return

    global bot_instance
    bot_instance = Bot(token=settings.BOT_TOKEN)
    dp = Dispatcher()
    dp.include_router(bot_router)

    retry_delay = 5
    while True:
        try:
            logger.info("Starting Telegram Bot polling (deleting existing webhook)...")
            await bot_instance.delete_webhook(drop_pending_updates=False)
            await dp.start_polling(bot_instance, handle_signals=False)
            logger.info("Bot polling stopped cleanly.")
            break
        except asyncio.CancelledError:
            logger.info("Bot polling cancelled.")
            break
        except Exception as e:
            logger.error(f"Telegram bot runtime error: {e}. Reconnecting in {retry_delay}s...")
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

        # Launch Telegram bot polling with auto-reconnect
        await run_bot_polling()

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
