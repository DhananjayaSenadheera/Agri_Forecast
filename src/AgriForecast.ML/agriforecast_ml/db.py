"""SQLAlchemy engine for the AgriForecast SQL Server database (via pymssql)."""
from sqlalchemy import create_engine
from sqlalchemy.engine import URL, Engine

from .config import get_db_settings


def get_engine() -> Engine:
    s = get_db_settings()
    url = URL.create(
        "mssql+pymssql",
        username=s["user"],
        password=s["password"],
        host=s["host"],
        port=s["port"],
        database=s["database"],
    )
    return create_engine(url, pool_pre_ping=True)
