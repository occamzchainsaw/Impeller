"""Packs rendered PNGs into a multi-resolution .ico.

Small sizes go in as uncompressed DIBs and the two large ones as PNG. That split is what every
icon editor emits, and it is the conservative reading of the format: PNG frames have been legal
since Vista, but the DIB path is the one every shell surface has always taken.

No third-party imaging library, so there is a small PNG reader here. It only has to handle what the
renderer produces: 8-bit RGBA, non-interlaced.
"""
import io
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))

DIB_SIZES = (16, 20, 24, 32, 40, 48, 64)
PNG_SIZES = (128, 256)


def read_png(path):
    """Returns (width, height, rgba bytes) for an 8-bit RGBA non-interlaced PNG."""
    data = io.open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', path

    pos = 8
    width = height = None
    idat = []

    while pos < len(data):
        length, kind = struct.unpack_from('>I4s', data, pos)
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length

        if kind == b'IHDR':
            width, height, depth, colour, _, _, interlace = struct.unpack('>IIBBBBB', body)
            assert depth == 8 and colour == 6 and interlace == 0, (path, depth, colour, interlace)
        elif kind == b'IDAT':
            idat.append(body)
        elif kind == b'IEND':
            break

    raw = zlib.decompress(b''.join(idat))
    stride = width * 4
    out = bytearray(stride * height)
    previous = bytearray(stride)
    pos = 0

    for y in range(height):
        filter_type = raw[pos]
        line = bytearray(raw[pos + 1:pos + 1 + stride])
        pos += 1 + stride

        for x in range(stride):
            a = line[x - 4] if x >= 4 else 0
            b = previous[x]
            c = previous[x - 4] if x >= 4 else 0

            if filter_type == 1:
                line[x] = (line[x] + a) & 0xFF
            elif filter_type == 2:
                line[x] = (line[x] + b) & 0xFF
            elif filter_type == 3:
                line[x] = (line[x] + ((a + b) >> 1)) & 0xFF
            elif filter_type == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pred) & 0xFF

        out[y * stride:(y + 1) * stride] = line
        previous = line

    return width, height, bytes(out)


def dib(width, height, rgba):
    """A 32-bit bottom-up DIB with the empty AND mask the format still insists on."""
    header = struct.pack(
        '<IiiHHIIiiII',
        40, width, height * 2, 1, 32, 0, width * height * 4, 0, 0, 0, 0)

    rows = []
    for y in range(height - 1, -1, -1):
        row = bytearray()
        for x in range(width):
            r, g, b, a = rgba[(y * width + x) * 4:(y * width + x) * 4 + 4]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))

    # 1 bit per pixel, padded to 4-byte rows, all zero: the alpha channel above is what is used,
    # but a missing mask makes the entry malformed rather than merely redundant.
    mask_stride = ((width + 31) // 32) * 4
    mask = bytes(mask_stride * height)

    return header + b''.join(rows) + mask


def pack(stem, out_path):
    entries = []

    for size in DIB_SIZES:
        w, h, rgba = read_png(os.path.join(HERE, '%s-%d.png' % (stem, size)))
        entries.append((size, dib(w, h, rgba)))

    for size in PNG_SIZES:
        entries.append((size, io.open(os.path.join(HERE, '%s-%d.png' % (stem, size)), 'rb').read()))

    out = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)

    for size, blob in entries:
        out += struct.pack(
            '<BBBBHHII',
            size if size < 256 else 0, size if size < 256 else 0, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    for _, blob in entries:
        out += blob

    io.open(out_path, 'wb').write(bytes(out))
    print('%s -> %d entries, %d bytes' % (os.path.basename(out_path), len(entries), len(out)))


if __name__ == '__main__':
    pack(sys.argv[1], sys.argv[2])
