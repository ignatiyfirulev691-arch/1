from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from statistics import mean
from typing import Iterable
from urllib.parse import quote
from urllib.request import Request, urlopen


@dataclass(frozen=True)
class WeatherData:
    city: str
    current_temp_c: int
    country: str


def load_cities(path: Path) -> list[str]:
    return [
        line.strip()
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]


def fetch_weather(city: str) -> WeatherData:
    url = f"https://wttr.in/{quote(city)}?format=j1"
    request = Request(
        url,
        headers={
            "User-Agent": "WeatherCitiesTask/1.0",
            "Accept": "application/json",
        },
    )

    with urlopen(request, timeout=20) as response:
        payload = json.load(response)

    current_temp_c = int(payload["current_condition"][0]["temp_C"])
    country = payload["nearest_area"][0]["country"][0]["value"]

    return WeatherData(
        city=city,
        current_temp_c=current_temp_c,
        country=country,
    )


def print_city_weather(items: Iterable[WeatherData]) -> None:
    print("Weather by city")
    print("---------------")
    for item in items:
        print(f"{item.city}, {item.country}: {item.current_temp_c:+d} °C")


def print_country_summary(items: list[WeatherData]) -> None:
    grouped: dict[str, list[WeatherData]] = {}

    for item in items:
        grouped.setdefault(item.country, []).append(item)

    print("\nSummary by country")
    print("------------------")

    for country in sorted(grouped):
        country_items = grouped[country]
        temperatures = [item.current_temp_c for item in country_items]

        print(
            f"{country} - "
            f"{len(country_items)} cities, "
            f"avg: {mean(temperatures):+.1f} °C, "
            f"min: {min(temperatures):+d} °C, "
            f"max: {max(temperatures):+d} °C"
        )


def main() -> None:
    cities_file = Path(__file__).with_name("Cities.txt")
    cities = load_cities(cities_file)

    weather_data: list[WeatherData] = []

    for city in cities:
        try:
            weather_data.append(fetch_weather(city))
        except Exception as exc:
            print(f"Failed to load weather for {city}: {exc}")

    if not weather_data:
        raise SystemExit("No weather data received.")

    print_city_weather(weather_data)
    print_country_summary(weather_data)


if __name__ == "__main__":
    main()
