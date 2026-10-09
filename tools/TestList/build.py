# Turns docs/test-list.md into a printable two-column checklist (HTML; print it to PDF).
# python3 tools/TestList/build.py docs/test-list.md out.html
import html, re, sys
src = open(sys.argv[1]).read().splitlines()
title = 'Test list'; intro = ''; S = []
for line in src:
    if line.startswith('# '): title = line[2:].strip()
    elif line.startswith('## '): S.append([line[3:].strip(), '', []])
    elif line.startswith('_') and S: S[-1][1] = line.strip('_ ')
    elif line.startswith('- [') and S:
        t = line[6:].strip(); how = ''
        if ' — ' in t: t, how = t.split(' — ', 1)
        S[-1][2].append((t, how, line[3] == 'x'))
    elif line.strip() and not S: intro = line.strip()
rows = []
for name, note, items in S:
    rows.append(f'<section><h2>{html.escape(name)}</h2>' + (f'<p class="n">{html.escape(note)}</p>' if note else '') + '<ul>')
    for i, (t, how, done) in enumerate(items):
        rows.append(f'<li{" class=keep" if i == 0 else ""}><span class="b">{"✓" if done else ""}</span><div><b>{html.escape(t)}</b>' + (f'<i>{html.escape(how)}</i>' if how else '') + '</div></li>')
    rows.append('</ul></section>')
total = sum(len(s[2]) for s in S)
page = f'''<!doctype html><html><head><meta charset="utf-8"><title>{html.escape(title)}</title><style>
@font-face{{font-family:Varela;src:url(varela.woff2)}}
@page{{size:Letter;margin:12mm 12mm}}
*{{box-sizing:border-box;margin:0;padding:0}}
body{{font-family:Varela,Helvetica,sans-serif;color:#151826;font-size:10.2pt}}
header{{display:flex;justify-content:space-between;align-items:flex-end;border-bottom:3px solid #1fa143;padding-bottom:6px;margin-bottom:8px}}
h1{{font-size:20pt}} header p{{color:#5b6075;font-size:9.3pt;text-align:right}}
.cols{{column-count:2;column-gap:9mm}}
section{{margin-bottom:9px}}
h2{{font-size:12pt;color:#178a37;margin:4px 0 2px;letter-spacing:.02em;break-after:avoid}}
.n{{color:#5b6075;font-size:8.7pt;margin-bottom:3px;break-after:avoid}}
ul{{list-style:none}}
li{{display:grid;grid-template-columns:14px 1fr;column-gap:7px;padding:3px 0;border-bottom:1px solid #e3e5ee;break-inside:avoid}}
.b{{width:12px;height:12px;border:1.6px solid #151826;border-radius:2px;margin-top:2px;font-size:9px;line-height:9px;text-align:center}}
li b{{font-weight:normal;display:block;line-height:1.24}}
li i{{font-style:normal;color:#5b6075;font-size:8.6pt;display:block;line-height:1.24}}
footer{{margin-top:6px;color:#5b6075;font-size:8.6pt;border-top:1px solid #e3e5ee;padding-top:4px}}
</style></head><body>
<header><div><h1>{html.escape(title)}</h1></div><p>The Button and mod 0.1.41 · Red Alert 0.3.9<br>{total} things to try in a real game · tick when it works, note what didn't</p></header>
<div class="cols">{"".join(rows)}</div>
<footer>If something fails: note the time, keep BepInEx/LogOutput.log (The Button → Settings → Open log) and Red Alert's log for stream items, and send them over.</footer>
</body></html>'''
open(sys.argv[2], 'w').write(page)
print(total)
