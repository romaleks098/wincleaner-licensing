import os
import asyncio
import logging
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
bot_instance = None

@asynccontextmanager
async def lifespan(app: FastAPI):
    global bot_task, bot_instance
    logger.info("Initializing database tables...")
    try:
        await init_db()
        logger.info("Database initialized successfully.")
    except Exception as e:
        logger.error(f"Error initializing database: {e}")

    logger.info(f"Ed25519 Public Key: {settings.ED25519_PUBLIC_KEY_HEX}")

    # Start Telegram bot polling if token provided
    if settings.BOT_TOKEN and len(settings.BOT_TOKEN) > 10:
        try:
            bot_instance = Bot(token=settings.BOT_TOKEN)
            dp = Dispatcher()
            dp.include_router(bot_router)

            async def run_bot():
                logger.info("Starting Telegram Bot polling...")
                try:
                    await bot_instance.delete_webhook(drop_pending_updates=False)
                    await dp.start_polling(bot_instance)
                except Exception as e:
                    logger.error(f"Telegram bot error: {e}")

            bot_task = asyncio.create_task(run_bot())
        except Exception as e:
            logger.error(f"Failed to start bot: {e}")
    else:
        logger.warning("BOT_TOKEN is not configured. Telegram bot is disabled.")

    yield

    if bot_task:
        bot_task.cancel()
        try:
            await bot_task
        except asyncio.CancelledError:
            pass
    if bot_instance:
        await bot_instance.session.close()
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

app.include_router(api_router)

if __name__ == "__main__":
    port = int(os.environ.get("PORT", settings.PORT))
    logger.info(f"Starting server on 0.0.0.0:{port}...")
    uvicorn.run(
        "main:app",
        host="0.0.0.0",
        port=port,
        reload=False
    )
