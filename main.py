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
    logger.info("FastAPI lifespan starting — launching background tasks...")

    async def background_startup():
        # Initialize DB in background without blocking HTTP healthcheck
        try:
            await init_db()
        except Exception as e:
            logger.error(f"Database startup error: {e}")

        # Start Telegram bot
        if settings.BOT_TOKEN and len(settings.BOT_TOKEN) > 10:
            try:
                global bot_instance
                bot_instance = Bot(token=settings.BOT_TOKEN)
                dp = Dispatcher()
                dp.include_router(bot_router)
                logger.info("Starting Telegram Bot polling...")
                await bot_instance.delete_webhook(drop_pending_updates=False)
                await dp.start_polling(bot_instance)
            except Exception as e:
                logger.error(f"Telegram bot runtime error: {e}")
        else:
            logger.warning("BOT_TOKEN is not configured. Telegram bot disabled.")

    bot_task = asyncio.create_task(background_startup())

    # Yield IMMEDIATELY so uvicorn binds to PORT and passes Render healthcheck in 1s
    yield

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
