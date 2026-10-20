"""Builds Source/Icons.cs from the owner's icon set - no icon of the window is drawn by hand.

    E:\\TOOLS\\Lucide-Icons    signs of actions (ISC): strokes on a 24 grid
    E:\\TOOLS\\Simple-Icons    brand marks (CC0): one filled path on a 24 grid, with the brand colour

Run:  py -3.12 Source\\Tools\\build_icons.py [-Sets E:\\TOOLS]
Add a name to LUCIDE or SIMPLE below and run it again; Icons.cs is never edited by hand.
The tool of Audion Build Publisher (Source\\Tools\\build_icons.py there), with this window's list; the brand marks as
Audion Browsers Portable draws them.
"""
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

SETS = pathlib.Path(sys.argv[sys.argv.index('-Sets') + 1]) if '-Sets' in sys.argv else pathlib.Path('E:/TOOLS')
OUT = pathlib.Path(__file__).resolve().parents[1] / 'Icons.cs'

# What the window shows. The key is the name in the set: a sign is asked for by it everywhere.
LUCIDE = [
    # Every sign the window names: in its code and markup - the sections, the buttons, the components of SETUP
    # (MainWindow.Setup.cs, SignOf). A sign nobody asks for is not carried.
    'app-window', 'audio-lines', 'audio-waveform', 'captions', 'chevron-down', 'chevron-right', 'clipboard-list', 'clipboard-paste', 'copy', 'download', 'eraser', 'file', 'file-audio', 'file-stack',
    'file-text', 'folder', 'folder-open', 'gpu', 'languages', 'maximize-2', 'minimize-2', 'package', 'pin',
    'pause', 'pin-off', 'play', 'power', 'repeat-2', 'rotate-ccw', 'scroll-text', 'settings', 'square', 'sun', 'trash-2', 'users', 'x',
]
# Signs of this program, drawn as Lucide draws (strokes on a 24 grid): Source\Tools\signs\NAME.svg. sm7b - the microphone
# of the mark of the program, in place of the schematic one of Lucide (the owner, 19.10.2026).
OWN = ['sm7b']
OWN_DIR = pathlib.Path(__file__).resolve().parent / 'signs'
# The marks of what the program carries, in their colours (the owner, 15.10.2026: "Пиктограммы брендов акцентируем
# цветом бренда"): SETUP names FFmpeg by its mark.
SIMPLE = ['ffmpeg']
SVG = '{http://www.w3.org/2000/svg}'


def number(value):
    text = ('%.4f' % float(value)).rstrip('0').rstrip('.')
    return text if text not in ('-0', '') else '0'


NUMBER = r'-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?'


def absolute_start(d):
    """The first "m" of a path is absolute whatever its case - but only inside its own path.

    Lucide starts paths with a small "m", and several paths are joined into one geometry here:
    the second "m" would then be counted from the end of the first drawing. It becomes "M", and
    the pairs that followed it - implicit relative lines - get their own "l".
    """
    hit = re.match(r'\s*m\s*(%s)[\s,]*(%s)\s*,?\s*' % (NUMBER, NUMBER), d)
    if not hit:
        return d
    rest = d[hit.end():]
    lead = 'l' if rest and (rest[0].isdigit() or rest[0] in '-.') else ''
    return 'M%s %s%s%s' % (hit.group(1), hit.group(2), lead, rest)


def shape_to_path(element):
    """One SVG primitive as path data, so a sign is a single geometry."""
    tag = element.tag.replace(SVG, '')
    get = lambda name, default=0.0: float(element.get(name, default))
    if tag == 'path':
        return absolute_start(element.get('d', '').strip())
    if tag == 'line':
        return 'M%s %sL%s %s' % tuple(number(get(n)) for n in ('x1', 'y1', 'x2', 'y2'))
    if tag in ('polyline', 'polygon'):
        points = re.findall(r'-?\d*\.?\d+', element.get('points', ''))
        pairs = ['%s %s' % (number(points[i]), number(points[i + 1])) for i in range(0, len(points) - 1, 2)]
        return 'M' + 'L'.join(pairs) + ('Z' if tag == 'polygon' else '')
    if tag in ('circle', 'ellipse'):
        cx, cy = get('cx'), get('cy')
        rx = get('r') if tag == 'circle' else get('rx')
        ry = get('r') if tag == 'circle' else get('ry')
        return 'M%s %sa%s %s 0 1 0 %s 0a%s %s 0 1 0 %s 0Z' % (
            number(cx - rx), number(cy), number(rx), number(ry), number(2 * rx), number(rx), number(ry), number(-2 * rx))
    if tag == 'rect':
        x, y, w, h = get('x'), get('y'), get('width'), get('height')
        rx = float(element.get('rx', element.get('ry', 0)))
        ry = float(element.get('ry', element.get('rx', 0)))
        rx, ry = min(rx, w / 2), min(ry, h / 2)
        if rx == 0 and ry == 0:
            return 'M%s %sh%sv%sh%sZ' % (number(x), number(y), number(w), number(h), number(-w))
        return ('M%s %sh%sa%s %s 0 0 1 %s %sv%sa%s %s 0 0 1 %s %sh%sa%s %s 0 0 1 %s %sv%sa%s %s 0 0 1 %s %sZ' % (
            number(x + rx), number(y), number(w - 2 * rx), number(rx), number(ry), number(rx), number(ry),
            number(h - 2 * ry), number(rx), number(ry), number(-rx), number(ry),
            number(-(w - 2 * rx)), number(rx), number(ry), number(-rx), number(-ry),
            number(-(h - 2 * ry)), number(rx), number(ry), number(rx), number(-ry)))
    return ''


def paths_of(file):
    root = ET.parse(file).getroot()
    parts = [shape_to_path(e) for e in root.iter() if e.tag.replace(SVG, '') in ('path', 'line', 'polyline', 'polygon', 'circle', 'ellipse', 'rect')]
    box = [float(v) for v in root.get('viewBox', '0 0 24 24').split()]
    return ' '.join(p for p in parts if p), box


def quote(text):
    return '"' + text.replace('\\', '\\\\').replace('"', '\\"') + '"'


def main():
    lines = [
        '// Generated by Source/Tools/build_icons.py from Lucide (ISC) and Simple Icons (CC0) - do not edit by hand.',
        'using System.Collections.Generic;',
        '',
        'namespace AudionVoiceTools;',
        '',
        '/// <summary>The signs of the window, by their names in the set they come from. Nothing here is drawn by hand.</summary>',
        'public static partial class Icons',
        '{',
        '    /// <summary>Lucide: strokes on a 24 grid.</summary>',
        '    public static readonly IReadOnlyDictionary<string, string> Lucide = new Dictionary<string, string>',
        '    {',
    ]
    for name in sorted(set(LUCIDE)):
        data, _ = paths_of(SETS / 'Lucide-Icons' / 'package' / 'icons' / (name + '.svg'))
        lines.append('        [%s] = %s,' % (quote(name), quote(data)))
    for name in sorted(set(OWN)):
        data, _ = paths_of(OWN_DIR / (name + '.svg'))
        lines.append('        [%s] = %s,' % (quote(name), quote(data)))
    lines += ['    };', '', '    /// <summary>Brand marks: name -> (brand colour, the side of the square it is drawn in, a filled path).</summary>',
              '    public static readonly IReadOnlyDictionary<string, (string Hex, double Box, string Path)> Brand = new Dictionary<string, (string, double, string)>', '    {']
    colours = {}
    for entry in json.load(open(SETS / 'Simple-Icons' / 'simple-icons.json', encoding='utf-8')):
        colours[re.sub(r'[^a-z0-9]', '', entry.get('slug', entry['title']).lower())] = '#' + entry['hex']
    for name in sorted(set(SIMPLE)):
        data, box = paths_of(SETS / 'Simple-Icons' / 'SVG' / (name + '.svg'))
        lines.append('        [%s] = (%s, %s, %s),' % (quote(name), quote(colours.get(name, '')), number(box[2]), quote(data)))
    lines += ['    };', '}', '']
    # The sources of this window end their lines with LF (Tests\Test-Window.ps1).
    OUT.write_text('\n'.join(lines), encoding='utf-8', newline='\n')
    print('Icons.cs: Lucide %d, own %d, brands %d' % (len(set(LUCIDE)), len(set(OWN)), len(set(SIMPLE))))


if __name__ == '__main__':
    main()
