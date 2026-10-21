import os
from PIL import Image, ImageDraw, ImageFont

ASSETS_DIR = os.path.join(os.path.dirname(__file__), "assets", "banners")
os.makedirs(ASSETS_DIR, exist_ok=True)

def get_font(size: int, bold: bool = False):
    font_names = (
        ["segoeuib.ttf", "arialbd.ttf", "tahomabd.ttf"] if bold
        else ["segoeui.ttf", "arial.ttf", "tahoma.ttf"]
    )
    for name in font_names:
        win_path = os.path.join(os.environ.get("WINDIR", "C:\\Windows"), "Fonts", name)
        if os.path.isfile(win_path):
            try:
                return ImageFont.truetype(win_path, size)
            except Exception:
                pass
    return ImageFont.load_default()

def draw_rounded_rect(draw, bbox, radius, fill=None, outline=None, width=1):
    x0, y0, x1, y1 = bbox
    draw.rounded_rectangle([x0, y0, x1, y1], radius=radius, fill=fill, outline=outline, width=width)

def draw_text_wrapped(draw, text, font, x, y, max_width, line_spacing, fill):
    lines = []
    for para in text.split("\n"):
        words = para.split(" ")
        cur_line = []
        for word in words:
            test_line = " ".join(cur_line + [word])
            bbox = font.getbbox(test_line)
            w = bbox[2] - bbox[0]
            if w <= max_width:
                cur_line.append(word)
            else:
                if cur_line:
                    lines.append(" ".join(cur_line))
                    cur_line = [word]
                else:
                    lines.append(word)
        if cur_line:
            lines.append(" ".join(cur_line))
    
    cur_y = y
    for line in lines:
        draw.text((x, cur_y), line, font=font, fill=fill)
        cur_y += line_spacing
    return cur_y

def create_base_canvas(width, height, color_top, color_bottom, accent_glow=None):
    base = Image.new("RGBA", (width, height), color_top)
    draw = ImageDraw.Draw(base)
    for y in range(height):
        factor = y / height
        r = int(color_top[0] * (1 - factor) + color_bottom[0] * factor)
        g = int(color_top[1] * (1 - factor) + color_bottom[1] * factor)
        b = int(color_top[2] * (1 - factor) + color_bottom[2] * factor)
        draw.line([(0, y), (width, y)], fill=(r, g, b, 255))
    
    # Ambient corner glow
    if accent_glow:
        glow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        gdraw = ImageDraw.Draw(glow)
        cx, cy = int(width * 0.88), int(height * 0.22)
        for rad in range(180, 0, -8):
            alpha = int((1 - rad / 180) * 45)
            gdraw.ellipse([cx - rad, cy - rad, cx + rad, cy + rad], fill=(*accent_glow[:3], alpha))
        base = Image.alpha_composite(base, glow)

    # Subtle grid dots (spacious, low opacity)
    grid = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(grid)
    for x in range(30, width, 40):
        for y in range(30, height, 40):
            gdraw.ellipse([x - 1, y - 1, x + 1, y + 1], fill=(255, 255, 255, 12))
    base = Image.alpha_composite(base, grid)
    return base


def generate_main_banner():
    width, height = 960, 500
    img = create_base_canvas(width, height, (10, 15, 26), (15, 23, 42), (0, 210, 255))
    draw = ImageDraw.Draw(img)

    # Outer border
    draw_rounded_rect(draw, [16, 16, width - 16, height - 16], 18, outline=(0, 210, 255, 80), width=2)

    # Badges (No emojis in text!)
    draw_rounded_rect(draw, [40, 36, 185, 72], 18, fill=(0, 210, 255, 30), outline=(0, 210, 255, 180), width=1)
    draw.text((58, 45), "OFFICIAL BOT", font=get_font(13, bold=True), fill=(0, 230, 255))

    draw_rounded_rect(draw, [198, 36, 310, 72], 18, fill=(147, 51, 234, 35), outline=(168, 85, 247, 180), width=1)
    draw.text((216, 45), "v2.0 PRO", font=get_font(13, bold=True), fill=(216, 180, 254))

    # Title & Subtitle
    draw.text((40, 92), "WinCleaner Security & Control", font=get_font(36, bold=True), fill=(255, 255, 255))
    draw.text((40, 145), "Официальный центр управления лицензиями и оптимизацией Windows", font=get_font(17, bold=False), fill=(203, 213, 225))

    # 3 Cards
    cards = [
        ("Мгновенная активация", "Автоматическая привязка к вашему ПК за пару секунд без ожидания"),
        ("Смена ПК / Сброс HWID", "Перенос лицензии на новый компьютер или после переустановки Windows — 150 ₽"),
        ("Анонимная поддержка", "Прямая связь с оператором прямо в диалоге бота 24/7 без раскрытия данных")
    ]

    card_w = 276
    card_h = 175
    start_x = 40
    card_y = 200

    for i, (head, body) in enumerate(cards):
        cx = start_x + i * (card_w + 24)
        is_hwid = (i == 1)
        border_col = (56, 189, 248, 180) if is_hwid else (56, 189, 248, 80)
        bg_col = (26, 40, 68, 240) if is_hwid else (20, 30, 50, 220)
        draw_rounded_rect(draw, [cx, card_y, cx + card_w, card_y + card_h], 14, fill=bg_col, outline=border_col, width=2 if is_hwid else 1)

        # Highlight tag on HWID card
        if is_hwid:
            draw_rounded_rect(draw, [cx + card_w - 75, card_y + 12, cx + card_w - 14, card_y + 34], 6, fill=(56, 189, 248, 50), outline=(56, 189, 248, 200))
            draw.text((cx + card_w - 65, card_y + 15), "150 ₽", font=get_font(12, bold=True), fill=(56, 189, 248))

        draw.text((cx + 18, card_y + 18), head, font=get_font(16, bold=True), fill=(255, 255, 255))
        draw_text_wrapped(draw, body, get_font(13, bold=False), cx + 18, card_y + 58, card_w - 36, 22, (203, 213, 225))

    # Clean bottom highlight bar (NO dev jargon!)
    draw_rounded_rect(draw, [40, 405, width - 40, 455], 10, fill=(15, 23, 42, 230), outline=(56, 189, 248, 60), width=1)
    
    # Draw green dot
    draw.ellipse([60, 427, 68, 435], fill=(74, 222, 128))
    draw.text((80, 420), "Глубокая очистка системы  •  Оптимизация реестра  •  Защита приватности  •  Поддержка 24/7",
              font=get_font(14, bold=True), fill=(241, 245, 249))

    out_path = os.path.join(ASSETS_DIR, "banner_main.png")
    img.convert("RGB").save(out_path, "PNG", quality=95)
    print(f"Generated: {out_path}")


def generate_tariffs_banner():
    width, height = 960, 540
    img = create_base_canvas(width, height, (16, 12, 32), (24, 18, 48), (168, 85, 247))
    draw = ImageDraw.Draw(img)

    # Frame border
    draw_rounded_rect(draw, [16, 16, width - 16, height - 16], 18, outline=(168, 85, 247, 90), width=2)

    # Header badge
    draw_rounded_rect(draw, [40, 32, 220, 68], 18, fill=(168, 85, 247, 40), outline=(168, 85, 247, 190), width=1)
    draw.text((58, 42), "ОФИЦИАЛЬНЫЕ ТАРИФЫ", font=get_font(13, bold=True), fill=(233, 213, 255))

    # Title & Subtitle
    draw.text((40, 84), "Выберите подходящую лицензию", font=get_font(34, bold=True), fill=(255, 255, 255))
    draw.text((40, 132), "Все лицензии включают полный доступ к функционалу WinCleaner и авто-обновлениям", font=get_font(15, bold=False), fill=(216, 180, 254))

    # 3 Tariff Cards
    tariffs = [
        ("1 МЕСЯЦ", "299 ₽", "1 компьютер", "Быстрый старт\nПолная очистка и разгон\nСтандартная поддержка", False),
        ("1 ГОД", "990 ₽", "1 компьютер", "ХИТ ПРОДАЖ (Экономия 72%)\nЦелый год чистой Windows\nПриоритетный саппорт 24/7", True),
        ("LIFETIME", "1 990 ₽", "2 компьютера", "Бессрочно (Навсегда)\nДля 2 ваших компьютеров\nVIP пожизненный доступ", False)
    ]

    card_w = 276
    card_h = 240
    start_x = 40
    card_y = 168

    for i, (name, price, pcs, desc, is_hit) in enumerate(tariffs):
        cx = start_x + i * (card_w + 24)
        border_col = (250, 204, 21, 230) if is_hit else (168, 85, 247, 100)
        bg_col = (42, 30, 70, 240) if is_hit else (28, 22, 50, 220)

        draw_rounded_rect(draw, [cx, card_y, cx + card_w, card_y + card_h], 14, fill=bg_col, outline=border_col, width=2 if is_hit else 1)

        # Hit badge
        if is_hit:
            draw_rounded_rect(draw, [cx + card_w - 110, card_y + 12, cx + card_w - 14, card_y + 36], 6, fill=(250, 204, 21, 255))
            draw.text((cx + card_w - 98, card_y + 16), "ТОП ВЫБОР", font=get_font(11, bold=True), fill=(15, 23, 42))

        draw.text((cx + 18, card_y + 18), name, font=get_font(18, bold=True), fill=(254, 240, 138) if is_hit else (255, 255, 255))
        draw.text((cx + 18, card_y + 48), price, font=get_font(32, bold=True), fill=(255, 255, 255))

        # Device badge
        draw_rounded_rect(draw, [cx + 18, card_y + 98, cx + 155, card_y + 124], 6, fill=(168, 85, 247, 40), outline=(168, 85, 247, 140))
        draw.text((cx + 26, card_y + 102), pcs, font=get_font(12, bold=True), fill=(233, 213, 255))

        # Bullets with custom dots
        lines = desc.split("\n")
        for line_idx, line in enumerate(lines):
            ly = card_y + 140 + line_idx * 30
            dot_color = (250, 204, 21) if is_hit else (168, 85, 247)
            draw.ellipse([cx + 18, ly + 6, cx + 24, ly + 12], fill=dot_color)
            draw.text((cx + 32, ly), line, font=get_font(12, bold=False), fill=(241, 245, 249))

    # Prominent Wide HWID Reset Card at bottom
    hwid_y = 428
    draw_rounded_rect(draw, [40, hwid_y, width - 40, hwid_y + 82], 12, fill=(22, 34, 60, 240), outline=(56, 189, 248, 160), width=2)
    
    # Left tag: 150 ₽
    draw_rounded_rect(draw, [56, hwid_y + 18, 155, hwid_y + 64], 8, fill=(56, 189, 248, 35), outline=(56, 189, 248, 200), width=1)
    draw.text((72, hwid_y + 26), "150 ₽", font=get_font(22, bold=True), fill=(56, 189, 248))

    draw.text((175, hwid_y + 18), "ПЕРЕНОС ЛИЦЕНЗИИ / СБРОС ПРИВЯЗКИ HWID", font=get_font(15, bold=True), fill=(255, 255, 255))
    draw.text((175, hwid_y + 44), "Смена материнской платы, переустановка Windows или перенос на другой ПК — заказывается в боте",
              font=get_font(13, bold=False), fill=(203, 213, 225))

    out_path = os.path.join(ASSETS_DIR, "banner_tariffs.png")
    img.convert("RGB").save(out_path, "PNG", quality=95)
    print(f"Generated: {out_path}")


def generate_support_banner():
    width, height = 960, 500
    img = create_base_canvas(width, height, (10, 20, 32), (16, 28, 46), (14, 165, 233))
    draw = ImageDraw.Draw(img)

    # Frame border
    draw_rounded_rect(draw, [16, 16, width - 16, height - 16], 18, outline=(14, 165, 233, 90), width=2)

    # Header badge
    draw_rounded_rect(draw, [40, 32, 250, 68], 18, fill=(14, 165, 233, 40), outline=(14, 165, 233, 190), width=1)
    draw.text((58, 42), "СЛУЖБА ПОДДЕРЖКИ 24/7", font=get_font(13, bold=True), fill=(186, 230, 253))

    # Title & Subtitle
    draw.text((40, 84), "Центр заботы о пользователях", font=get_font(34, bold=True), fill=(255, 255, 255))
    draw.text((40, 132), "Напишите нам прямо в этот диалог — оператор поддержки ответит вам от лица бота", font=get_font(16, bold=False), fill=(203, 213, 225))

    # Support features (Clean rendered bullets)
    items = [
        ("Полная конфиденциальность", "Ваш диалог полностью защищен и изолирован внутри системы без раскрытия личных данных"),
        ("Быстрый ответ специалиста", "Среднее время ответа оператора поддержки в чате — от 5 до 15 минут"),
        ("Сброс HWID и смена оборудования (150 ₽)", "Помощь со сменой материнской платы, SSD/HDD или переустановкой Windows"),
        ("Отправка скриншотов и файлов", "Вы можете просто прикрепить скриншот ошибки или текстовый лог прямо в этот чат")
    ]

    for idx, (head, sub) in enumerate(items):
        y = 180 + idx * 70
        # Card backing for each row
        draw_rounded_rect(draw, [40, y, width - 40, y + 58], 10, fill=(18, 30, 50, 220), outline=(56, 189, 248, 60), width=1)

        # Glowing cyan bullet
        draw.ellipse([60, y + 23, 72, y + 35], fill=(56, 189, 248))

        draw.text((88, y + 10), head, font=get_font(16, bold=True), fill=(255, 255, 255))
        draw.text((88, y + 33), sub, font=get_font(13, bold=False), fill=(203, 213, 225))

    out_path = os.path.join(ASSETS_DIR, "banner_support.png")
    img.convert("RGB").save(out_path, "PNG", quality=95)
    print(f"Generated: {out_path}")


def generate_activate_banner():
    width, height = 960, 500
    img = create_base_canvas(width, height, (14, 22, 34), (20, 32, 48), (34, 197, 94))
    draw = ImageDraw.Draw(img)

    # Frame border
    draw_rounded_rect(draw, [16, 16, width - 16, height - 16], 18, outline=(34, 197, 94, 90), width=2)

    # Badge
    draw_rounded_rect(draw, [40, 32, 230, 68], 18, fill=(34, 197, 94, 40), outline=(34, 197, 94, 190), width=1)
    draw.text((58, 42), "БЫСТРАЯ АКТИВАЦИЯ", font=get_font(13, bold=True), fill=(187, 247, 208))

    # Title & Subtitle
    draw.text((40, 84), "Как активировать WinCleaner", font=get_font(34, bold=True), fill=(255, 255, 255))
    draw.text((40, 132), "Всего 3 простых шага до полностью оптимизированной и быстрой системы", font=get_font(16, bold=False), fill=(203, 213, 225))

    # Steps cards
    steps = [
        ("ШАГ 1", "Запустите программу", "Запустите файл WinCleaner.exe от имени администратора на вашем компьютере."),
        ("ШАГ 2", "Вставьте ключ", "В появившемся окне вставьте ваш ключ вида WINC-XXXX-XXXX-XXXX."),
        ("ШАГ 3", "Готово!", "Нажмите «Активировать». Программа привяжет ваш ПК и разблокирует весь функционал.")
    ]

    card_w = 276
    card_h = 210
    start_x = 40
    card_y = 180

    for i, (step_num, step_title, step_desc) in enumerate(steps):
        cx = start_x + i * (card_w + 24)
        draw_rounded_rect(draw, [cx, card_y, cx + card_w, card_y + card_h], 14, fill=(22, 36, 56, 230), outline=(34, 197, 94, 90), width=1)

        # Step badge
        draw_rounded_rect(draw, [cx + 18, card_y + 18, cx + 90, card_y + 44], 6, fill=(34, 197, 94, 40), outline=(34, 197, 94, 180))
        draw.text((cx + 28, card_y + 22), step_num, font=get_font(12, bold=True), fill=(187, 247, 208))

        draw.text((cx + 18, card_y + 60), step_title, font=get_font(17, bold=True), fill=(255, 255, 255))
        draw_text_wrapped(draw, step_desc, get_font(13, bold=False), cx + 18, card_y + 95, card_w - 36, 22, (203, 213, 225))

    # Bottom note
    draw_rounded_rect(draw, [40, 415, width - 40, 465], 10, fill=(18, 30, 48, 240), outline=(34, 197, 94, 70), width=1)
    draw.ellipse([60, 437, 68, 445], fill=(34, 197, 94))
    draw.text((80, 430), "При смене компьютера или переустановке Windows перенос лицензии (сброс HWID) — 150 ₽ в боте",
              font=get_font(13, bold=True), fill=(241, 245, 249))

    out_path = os.path.join(ASSETS_DIR, "banner_activate.png")
    img.convert("RGB").save(out_path, "PNG", quality=95)
    print(f"Generated: {out_path}")

if __name__ == "__main__":
    generate_main_banner()
    generate_tariffs_banner()
    generate_support_banner()
    generate_activate_banner()
    print("All refreshed banners generated cleanly!")
