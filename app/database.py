import asyncio
import logging
from typing import AsyncGenerator
from sqlalchemy.ext.asyncio import create_async_engine, AsyncSession, async_sessionmaker
from sqlalchemy.orm import declarative_base
from app.config import settings

logger = logging.getLogger("database")
Base = declarative_base()

engine = create_async_engine(
    settings.async_database_url,
    echo=False,
    pool_pre_ping=True
)

AsyncSessionLocal = async_sessionmaker(
    bind=engine,
    class_=AsyncSession,
    expire_on_commit=False,
    autocommit=False,
    autoflush=False
)

async def init_db():
    for attempt in range(1, 15):
        try:
            logger.info(f"Connecting to database (attempt {attempt}/15)...")
            async with engine.begin() as conn:
                await conn.run_sync(Base.metadata.create_all)
            logger.info("Database connected and schema initialized.")
            return
        except Exception as e:
            logger.warning(f"Database connection attempt {attempt} failed: {e}")
            if attempt == 15:
                logger.error("Could not connect to database after 15 attempts.")
                raise e
            await asyncio.sleep(2)

async def get_db() -> AsyncGenerator[AsyncSession, None]:
    async with AsyncSessionLocal() as session:
        try:
            yield session
        finally:
            await session.close()
