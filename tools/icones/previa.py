import re, sys, cairosvg
sys.path.insert(0, sys.argv[1])
from icones import ICONES
from ribbon import COMANDOS, SELOS, CATEGORIAS, AMBAR

import os
SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'src', 'Civil3D2026Plugin', 'Infrastructure') + os.sep
names = open(SRC + 'CommandNames.cs').read()
cls = None; const = {}
for line in names.splitlines():
    m = re.match(r'\s*public static class (\w+)', line)
    if m: cls = m.group(1)
    m = re.search(r'public const string (\w+) = "([^"]+)"', line)
    if m and cls: const[f'{cls}.{m.group(1)}'] = m.group(2)
cat = open(SRC + 'CommandCatalog.cs').read()
cmds = []
for mod in re.finditer(r'new\(\s*"([^"]+)",\s*"([^"]+)"(.*?)\}\)', cat, re.S):
    categoria = mod.group(1)
    for m in re.finditer(r'new CommandDefinition\(\s*(?:C3DCommands\.([\w.]+)|"([^"]+)"),\s*"([^"]*)"', mod.group(3)):
        nome = const.get(m.group(1), m.group(1)) if m.group(1) else m.group(2)
        cmds.append((categoria, nome, m.group(3)))

def camadas(key, L, A, F, S, W, esp=1.3):
    out = []
    for camada, d in ICONES[key]:
        if camada == 'L': out.append(f'<path d="{d}" fill="none" stroke="{L}" stroke-width="{esp}" stroke-linecap="round" stroke-linejoin="round"/>')
        elif camada == 'A': out.append(f'<path d="{d}" fill="none" stroke="{A}" stroke-width="{esp}" stroke-linecap="round" stroke-linejoin="round"/>')
        elif camada == 'F': out.append(f'<path d="{d}" fill="{F}" stroke="{F}" stroke-width="0.35" stroke-linejoin="round"/>')
        elif camada == 'S': out.append(f'<path d="{d}" fill="{S}"/>')
        elif camada == 'W': out.append(f'<path d="{d}" fill="{W}"/>')
    return ''.join(out)

def painel(key, x, y, s, fundo, texto, acento):
    return f'<g transform="translate({x},{y}) scale({s})">{camadas(key, texto, acento, acento, AMBAR, texto)}</g>'

def ribbon(categoria, key, selo, x, y, s):
    c1, c2, tinta = CATEGORIAS.get(categoria, CATEGORIAS['Geral'])
    gid = f'g{abs(hash((categoria,x,y)))}'
    g = [f'<defs><linearGradient id="{gid}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{c1}"/><stop offset="1" stop-color="{c2}"/></linearGradient></defs>']
    g.append(f'<rect x="0.3" y="0.3" width="15.4" height="15.4" rx="3.4" fill="url(#{gid})"/>')
    grade = ''.join(f'M{v},1 V15 M1,{v} H15 ' for v in (4, 8, 12))
    g.append(f'<path d="{grade}" stroke="#FFFFFF" stroke-opacity="0.07" stroke-width="0.3"/>')
    g.append('<path d="M3.4,0.9 H12.6" stroke="#FFFFFF" stroke-opacity="0.25" stroke-width="0.5" stroke-linecap="round"/>')
    esc, dx = (0.74, -0.5) if selo else (0.8, 0)
    g.append(f'<g transform="translate({8+dx},{8+dx}) scale({esc}) translate(-8,-8)">{camadas(key, "#F4F8FC", tinta, tinta, AMBAR, "#F4F8FC", 1.35)}</g>')
    if selo:
        g.append(f'<circle cx="12.2" cy="12.2" r="3.25" fill="{AMBAR}" stroke="{c2}" stroke-width="0.8"/>')
        g.append(f'<g transform="translate(12.2,12.2) scale(0.85) translate(-12.2,-12.2)"><path d="{SELOS[selo]}" fill="none" stroke="#2A2410" stroke-width="0.9" stroke-linecap="round" stroke-linejoin="round"/></g>')
    return f'<g transform="translate({x},{y}) scale({s})">{"".join(g)}</g>'

def salvar(svg, w, h, nome, fundo):
    doc = f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}"><rect width="100%" height="100%" fill="{fundo}"/>{svg}</svg>'
    cairosvg.svg2png(bytestring=doc.encode(), write_to=nome)

# 1. Painel: escuro e claro, 16 px ampliado 3x e o tamanho real
keys = list(ICONES)
col = 8; cw = 150; ch = 78
for tema, fundo, texto, acento, rot in [("escuro", "#252526", "#DEDEDE", "#50A0FF", "#AAAAAA"), ("claro", "#FFFFFF", "#141414", "#005FAA", "#555555")]:
    out = []
    for i, k in enumerate(keys):
        x = (i % col) * cw + 12; y = (i // col) * ch + 10
        out.append(painel(k, x, y, 3, fundo, texto, acento))
        out.append(painel(k, x + 60, y + 16, 1, fundo, texto, acento))
        out.append(f'<text x="{x}" y="{y+66}" font-family="DejaVu Sans" font-size="11" fill="{rot}">{k}</text>')
    rows = (len(keys) + col - 1) // col
    salvar(''.join(out), col * cw + 10, rows * ch + 16, f'{sys.argv[1]}/painel_{tema}.png', fundo)

# 2. Ribbon: 32 px (ampliado 2x) e 16 px real, por modulo
out = []; y = 10
ultimo = None; x = 10
for categoria, nome, titulo in cmds:
    key, selo = COMANDOS.get(nome, ("generico", None))
    if categoria != ultimo:
        if ultimo is not None: y += 104
        x = 10; ultimo = categoria
        out.append(f'<text x="10" y="{y+12}" font-family="DejaVu Sans" font-size="13" font-weight="bold" fill="#E8E8E8">{categoria}</text>')
    if x > 1500:
        x = 10; y += 104
    out.append(ribbon(categoria, key, selo, x, y + 20, 4))
    out.append(ribbon(categoria, key, selo, x + 68, y + 20, 1))
    out.append(f'<text x="{x}" y="{y+100}" font-family="DejaVu Sans" font-size="9.5" fill="#BDBDBD">{titulo[:16]}</text>')
    x += 110
salvar(''.join(out), 1620, y + 120, f'{sys.argv[1]}/ribbon.png', "#2B2B2B")
faltando = [n for _, n, _ in cmds if n not in COMANDOS]
print("comandos", len(cmds), "sem icone:", faltando)
