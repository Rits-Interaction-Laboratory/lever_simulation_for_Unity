import csv, math, pathlib
folder=pathlib.Path('ContactValidation')
paths=[folder/'contact_20261117_190809_0234.csv',folder/'contact_20261117_191209_3129.csv']
series=[]
for p in paths:
    with p.open(encoding='utf-8-sig') as f:
        series.append([(float(r['time_s'])-.5,float(r['torque_nm'])) for r in csv.DictReader(f)])
parts=['<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="720" viewBox="0 0 1200 720"><rect width="1200" height="720" fill="#f8fafc"/><g font-family="Arial,sans-serif" fill="#0f172a"><text x="70" y="38" font-size="24" font-weight="bold">Raw contact reaction torque: controlled cam sweep</text><text x="70" y="65" font-size="16">-60 to +60 degrees at 60 deg/s; k = 5000 N/m; damping = 60 Ns/m; dt = 0.5 ms</text>']
for panel,(data,label,color) in enumerate(zip(series,['Before: 108 separate flat contact faces','After: 320 faces with continuous contact normals'],['#64748b','#0284c7'])):
    x0,y0,w,h=85,120+panel*285,1050,220
    parts.append(f'<text x="{x0}" y="{y0-15}" font-size="18" font-weight="bold">{label}</text>')
    for val in [-.3,-.15,0,.15,.3]:
        y=y0+h/2-val/.35*h/2
        parts.append(f'<path d="M{x0} {y:.2f}h{w}" stroke="#cbd5e1"/><text x="{x0-10}" y="{y+5:.2f}" text-anchor="end" font-size="13">{val:+.2f}</text>')
    for val in [0,.5,1,1.5,2]:
        x=x0+val/2*w
        parts.append(f'<path d="M{x:.2f} {y0}v{h}" stroke="#e2e8f0"/><text x="{x:.2f}" y="{y0+h+22}" text-anchor="middle" font-size="13">{val:g}</text>')
    pts=' '.join(f'{x0+t/2*w:.2f},{y0+h/2-v/.35*h/2:.2f}' for t,v in data)
    parts.append(f'<polyline points="{pts}" fill="none" stroke="{color}" stroke-width="1.5"/><text x="{x0+w/2}" y="{y0+h+43}" text-anchor="middle" font-size="14">Sweep time [s]</text>')
parts.append('<text x="70" y="690" font-size="15">No temporal filtering. Fine variation RMS: 0.002424 to 0.000428 Nm (-82%). Max sample step: -85%.</text></g></svg>')
(folder/'torque_before_after.svg').write_text(''.join(parts),encoding='utf-8')
print(folder/'torque_before_after.svg')
