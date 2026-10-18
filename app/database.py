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
    global engine, AsyncSessionLocal
    for attempt in range(1, 6):
        try:
            logger.info(f"Connecting to database (attempt {attempt}/5)...")
            async with engine.begin() as conn:
                await conn.run_sync(Base.metadata.create_all)
            logger.info("Database connected and schema initialized.")
            return
        except Exception as e:
            logger.warning(f"Database connection attempt {attempt} failed: {e}")
            await asyncio.sleep(2)

    logger.warning("PostgreSQL unavailable — switching to local SQLite fallback database...")
    try:
        engine = create_async_engine("sqlite+aiosqlite:///./licensing.db", echo=False)
        AsyncSessionLocal.configure(bind=engine)
        async with engine.begin() as conn:
            await conn.run_sync(Base.metadata.create_all)
        logger.info("SQLite fallback database initialized successfully.")
    except Exception as e:
        logger.error(f"Fallback SQLite error: {e}")

async def get_db() -> AsyncGenerator[AsyncSession, None]:
    async with AsyncSessionLocal() as session:
        try:
            yield session
        finally:
            await session.close()
