import os
import math
from PIL import Image, ImageDraw, ImageFont, ImageFilter

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

def create_gradient(width, height, color_top, color_bottom, color_accent=None):
    base = Image.new("RGBA", (width, height), color_top)
    draw = ImageDraw.Draw(base)
    for y in range(height):
        factor = y / height
        r = int(color_top[0] * (1 - factor) + color_bottom[0] * factor)
        g = int(color_top[1] * (1 - factor) + color_bottom[1] * factor)
        b = int(color_top[2] * (1 - factor) + color_bottom[2] * factor)
        draw.line([(0, y), (width, y)], fill=(r, g, b, 255))
    
    # Overlay subtle accent glow
    if color_accent:
        glow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        gdraw = ImageDraw.Draw(glow)
        cx, cy = int(width * 0.85), int(height * 0.25)
        for rad in range(160, 0, -8):
            alpha = int((1 - rad / 160) * 45)
            gdraw.ellipse([cx - rad, cy - rad, cx + rad, cy + rad], fill=(*color_accent[:3], alpha))
        base = Image.alpha_composite(base, glow)
    
    # Subtle cyber grid dots
    grid_overlay = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    gdraw = ImageDraw.Draw(grid_overlay)
    for x in range(20, width, 40):
        for y in range(20, height, 40):
            gdraw.ellipse([x - 1, y - 1, x + 1, y + 1], fill=(255, 255, 255, 18))
    base = Image.alpha_composite(base, grid_overlay)
    return base

def generate_main_banner():
    width, height = 800, 400
    img = create_gradient(width, height, (11, 15, 25), (15, 23, 42), (0, 210, 255))
    draw = ImageDraw.Draw(img)

    # Frame border
    draw_rounded_rect(draw, [15, 15, width - 15, height - 15], 16, outline=(0, 210, 255, 80), width=2)
    
    # Glow badge: "OFFICIAL BOT"
    draw_rounded_rect(draw, [40, 40, 190, 72], 16, fill=(0, 210, 255, 30), outline=(0, 210, 255, 180), width=1)
    badge_font = get_font(14, bold=True)
    draw.text((60, 48), "🛡️ OFFICIAL BOT", font=badge_font, fill=(0, 225, 255))

    # Version badge
    draw_rounded_rect(draw, [205, 40, 315, 72], 16, fill=(121, 40, 202, 35), outline=(147, 51, 234, 180), width=1)
    draw.text((225, 48), "⚡ v2.0 PRO", font=badge_font, fill=(192, 132, 252))

    # Title
    title_font = get_font(38, bold=True)
    draw.text((40, 95), "WinCleaner Security & Control", font=title_font, fill=(255, 255, 255))

    # Subtitle
    sub_font = get_font(18, bold=False)
    draw.text((40, 150), "Официальный центр управления лицензиями и устройствами", font=sub_font, fill=(148, 163, 184))

    # 3 Feature cards
    features = [
        ("⚡ Мгновенная активация", "Автопривязка HWID ПК за секунду"),
        ("🔄 Свободный перенос", "1-клик сброс при смене Windows или ПК"),
        ("🛡️ Анонимная поддержка", "Прямая связь с разработчиками 24/7")
    ]
    
    card_width = 224
    card_height = 110
    start_x = 40
    card_y = 205

    for i, (f_title, f_desc) in enumerate(features):
        cx = start_x + i * (card_width + 24)
        draw_rounded_rect(draw, [cx, card_y, cx + card_width, card_y + card_height], 12,
                          fill=(25, 35, 55, 200), outline=(56, 189, 248, 70), width=1)
        draw.text((cx + 14, card_y + 18), f_title, font=get_font(15, bold=True), fill=(241, 245, 249))
        draw.text((cx + 14, card_y + 48), f_desc, font=get_font(13, bold=False), fill=(148, 163, 184))

    # Bottom status bar
    draw_rounded_rect(draw, [40, 340, width - 40, 375], 8, fill=(15, 23, 42, 220), outline=(30, 41, 59, 255), width=1)
    status_font = get_font(13, bold=False)
    draw.text((55, 348), "🟢 Сервер лицензирования: Online  |  🔒 Шифрование: Ed25519  |  🤖 Aiogram 3.x", font=status_font, fill=(52, 211, 153))

    out_path = os.path.join(ASSETS_DIR, "banner_main.png")
    img.convert("RGB").save(out_path, "PNG", quality=95)
    print(f"Generated: {out_path}")

def generate_tariffs_banner():
    width, height = 800, 400
    img = create_gradient(width, height, (15, 12, 28), (24, 18, 43), (168, 85, 247))
    draw = ImageDraw.Draw(img)

    # Frame border
    draw_rounded_rect(draw, [15, 15, width - 15, height - 15], 16, outline=(168, 85, 247, 80), width=2)

    # Header badge
    draw_rounded_rect(draw, [40, 35, 210, 67], 16, fill=(168, 85, 247, 30), outline=(168, 85, 247, 180), width=1)
    draw.text((58, 43), "💳 ОФИЦИАЛЬНЫЕ ТАРИФЫ", font=get_font(13, bold=True), fill=(216, 180, 254))

    # Title
    title_font = get_font(32, bold=True)
    draw.text((40, 80), "Выберите подходящую лицензию", font=title_font, fill=(255, 255, 255))
    draw.text((40, 125), "Все тарифы включают полный доступ к функционалу WinCleaner и авто-обновлениям", font=get_font(14, bold=False), fill=(167, 139, 250))

    # 3 Tariff Cards
    tariffs = [
        ("🌟 1 МЕСЯЦ", "299 ₽", "1 ПК", "Быстрый старт\nПолный доступ\nСтандартная поддержка", False),
        ("🚀 1 ГОД", "990 ₽", "1 ПК", "ХИТ ПРОДАЖ!\nЭкономия 72%\nПриоритетный саппорт", True),
        ("👑 LIFETIME", "1 990 ₽", "2 ПК", "Навсегда (без лимита)\n2 компьютера\nVIP обновления", False)
    ]

    card_width = 224
    card_height = 205
    start_x = 40
    card_y = 160

    for i, (name, price, pcs, desc, is_hit) in enumerate(tariffs):
        cx = start_x + i * (card_width + 24)
        border_color = (234, 179, 8, 220) if is_hit else (168, 85, 247, 90)
        bg_color = (38, 28, 62, 230) if is_hit else (24, 20, 42, 210)
        
        draw_rounded_rect(draw, [cx, card_y, cx + card_width, card_y + card_height], 14,
                          fill=bg_color, outline=border_color, width=2 if is_hit else 1)

        if is_hit:
            draw_rounded_rect(draw, [cx + 120, card_y - 12, cx + card_width - 10, card_y + 12], 8,
                              fill=(234, 179, 8, 255))
            draw.text((cx + 128, card_y - 8), "ТОП ВЫБОР", font=get_font(11, bold=True), fill=(0, 0, 0))

        # Tariff name
        draw.text((cx + 16, card_y + 16), name, font=get_font(16, bold=True), fill=(243, 232, 255) if not is_hit else (254, 240, 138))
        
        # Price
        draw.text((cx + 16, card_y + 44), price, font=get_font(28, bold=True), fill=(255, 255, 255))
        
        # Devices badge
        draw_rounded_rect(draw, [cx + 16, card_y + 88, cx + 80, card_y + 110], 6, fill=(147, 51, 234, 40), outline=(147, 51, 234, 150))
        draw.text((cx + 25, card_y + 92), pcs, font=get_font(12, bold=True), fill=(216, 180, 254))

        # Bullet points
        lines = desc.split("\n")
        for line_idx, line in enumerate(lines):
            draw.text((cx + 16, card_y + 125 + line_idx * 24), f"• {line}", font=get_font(13, bold=False), fill=(203, 213, 225))

    out_path = os.path.join(ASSETS_DIR, "banner_tariffs.png")
    img.convert("RGB").save(out_path, "PNG", quality=95)
    print(f"Generated: {out_path}")

def generate_support_banner():
    width, height = 800, 400
    img = create_gradient(width, height, (10, 20, 30), (15, 30, 45), (14, 165, 233))
    draw = ImageDraw.Draw(img)

    # Frame border
    draw_rounded_rect(draw, [15, 15, width - 15, height - 15], 16, outline=(14, 165, 233, 80), width=2)

    # Badge
    draw_rounded_rect(draw, [40, 35, 250, 67], 16, fill=(14, 165, 233, 30), outline=(14, 165, 233, 180), width=1)
    draw.text((58, 43), "💬 СЛУЖБА ПОДДЕРЖКИ 24/7", font=get_font(13, bold=True), fill=(125, 211, 252))

    # Title
    title_font = get_font(34, bold=True)
    draw.text((40, 85), "Центр заботы о пользователях", font=title_font, fill=(255, 255, 255))
    draw.text((40, 135), "Напишите нам прямо в бота — оператор ответит вам без посредников", font=get_font(16, bold=False), fill=(148, 163, 184))

    # Support features
    items = [
        ("🔒 Полная конфиденциальность", "Ваш диалог полностью защищен и изолирован внутри системы"),
        ("⚡ Быстрый ответ специалиста", "Среднее время ответа оператора поддержки — до 15 минут"),
        ("🛠️ Помощь по любым вопросам", "Активация, сброс HWID, перенос на новый ПК, ошибки Windows"),
        ("📎 Отправка скриншотов и логов", "Вы можете просто отправить фото или файл лога прямо в чат")
    ]

    for idx, (head, sub) in enumerate(items):
        y = 185 + idx * 48
        draw.text((50, y), "•", font=get_font(20, bold=True), fill=(56, 189, 248))
        draw.text((75, y), head, font=get_font(15, bold=True), fill=(241, 245, 249))
        draw.text((75, y + 20), sub, font=get_font(13, bold=False), fill=(148, 163, 184))

    out_path = os.path.join(ASSETS_DIR, "banner_support.png")
    img.convert("RGB").save(out_path, "PNG", quality=95)
    print(f"Generated: {out_path}")

def generate_activate_banner():
    width, height = 800, 400
    img = create_gradient(width, height, (16, 24, 39), (20, 30, 48), (34, 197, 94))
    draw = ImageDraw.Draw(img)

    # Frame border
    draw_rounded_rect(draw, [15, 15, width - 15, height - 15], 16, outline=(34, 197, 94, 80), width=2)

    # Badge
    draw_rounded_rect(draw, [40, 35, 230, 67], 16, fill=(34, 197, 94, 30), outline=(34, 197, 94, 180), width=1)
    draw.text((58, 43), "⚡ БЫСТРАЯ АКТИВАЦИЯ", font=get_font(13, bold=True), fill=(134, 239, 172))

    # Title
    title_font = get_font(34, bold=True)
    draw.text((40, 85), "Как активировать WinCleaner", font=title_font, fill=(255, 255, 255))
    draw.text((40, 135), "Всего 3 простых шага до чистой и быстрой операционной системы", font=get_font(16, bold=False), fill=(148, 163, 184))

    # Steps cards
    steps = [
        ("Шаг 1", "Запустите файл", "Запустите файл программы WinCleaner.exe от имени администратора"),
        ("Шаг 2", "Вставьте ключ", "В окне вставьте ваш лицензионный ключ вида WINC-XXXX-XXXX-XXXX"),
        ("Шаг 3", "Готово!", "Нажмите «Активировать». Программа привяжет HWID и запустится!")
    ]

    card_width = 224
    card_height = 165
    start_x = 40
    card_y = 185

    for i, (step_num, step_title, step_desc) in enumerate(steps):
        cx = start_x + i * (card_width + 24)
        draw_rounded_rect(draw, [cx, card_y, cx + card_width, card_y + card_height], 12,
                          fill=(25, 38, 55, 220), outline=(34, 197, 94, 90), width=1)
        
        # Step number badge
        draw_rounded_rect(draw, [cx + 14, card_y + 14, cx + 80, card_y + 40], 6, fill=(34, 197, 94, 40), outline=(34, 197, 94, 180))
        draw.text((cx + 24, card_y + 18), step_num, font=get_font(12, bold=True), fill=(134, 239, 172))

        draw.text((cx + 14, card_y + 52), step_title, font=get_font(16, bold=True), fill=(255, 255, 255))
        
        # Word wrap desc
        lines = []
        words = step_desc.split()
        cur_line = []
        for word in words:
            cur_line.append(word)
            if len(" ".join(cur_line)) > 24:
                lines.append(" ".join(cur_line[:-1]))
                cur_line = [word]
        if cur_line:
            lines.append(" ".join(cur_line))
        
        for l_idx, line in enumerate(lines):
            draw.text((cx + 14, card_y + 82 + l_idx * 20), line, font=get_font(12, bold=False), fill=(148, 163, 184))

    out_path = os.path.join(ASSETS_DIR, "banner_activate.png")
    img.convert("RGB").save(out_path, "PNG", quality=95)
    print(f"Generated: {out_path}")

if __name__ == "__main__":
    generate_main_banner()
    generate_tariffs_banner()
    generate_support_banner()
    generate_activate_banner()
    print("All banners generated successfully!")
