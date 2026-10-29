"""零依赖 PNG 像素采样：只用于核对 UI 截图里的颜色是否真的套上了 Token。

不用 PIL（本机没装），自己解 IHDR/IDAT + 反滤波，够用且不引入依赖。
用法：python png_probe.py <png> x,y [x,y ...]
"""
import sys
import zlib


def load_png(path):
    data = open(path, "rb").read()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a png"

    pos = 8
    width = height = bitdepth = colortype = None
    idat = bytearray()

    while pos < len(data):
        length = int.from_bytes(data[pos:pos + 4], "big")
        ctype = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length

        if ctype == b"IHDR":
            width = int.from_bytes(body[0:4], "big")
            height = int.from_bytes(body[4:8], "big")
            bitdepth = body[8]
            colortype = body[9]
        elif ctype == b"IDAT":
            idat += body
        elif ctype == b"IEND":
            break

    assert bitdepth == 8, f"only 8-bit supported, got {bitdepth}"
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colortype]
    raw = zlib.decompress(bytes(idat))
    stride = width * channels

    out = bytearray(width * height * channels)
    prev = bytearray(stride)
    src = 0

    for y in range(height):
        filt = raw[src]
        src += 1
        line = bytearray(raw[src:src + stride])
        src += stride

        if filt == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif filt == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif filt == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif filt == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 0xFF

        out[y * stride:(y + 1) * stride] = line
        prev = line

    return width, height, channels, out


def pixel(img, x, y):
    width, height, channels, buf = img
    x = max(0, min(width - 1, x))
    y = max(0, min(height - 1, y))
    off = (y * width + x) * channels
    r, g, b = buf[off], buf[off + 1], buf[off + 2]
    return f"#{r:02X}{g:02X}{b:02X}"


if __name__ == "__main__":
    image = load_png(sys.argv[1])
    print(f"size {image[0]}x{image[1]} channels={image[2]}")
    for arg in sys.argv[2:]:
        xs, ys = arg.split(",")
        print(f"  ({xs},{ys}) = {pixel(image, int(xs), int(ys))}")
