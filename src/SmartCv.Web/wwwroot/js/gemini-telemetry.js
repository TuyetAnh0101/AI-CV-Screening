/* Progressive visualisation only. Auth/JWT/API calls remain in Blazor, never in JS. */
(() => {
  "use strict";
  const known = new WeakMap();
  const num = value => Number.isFinite(Number(value)) ? Math.max(0, Number(value)) : 0;
  function graph(canvas, raw, unit) {
    if (!(canvas instanceof HTMLCanvasElement) || !canvas.isConnected) return;
    const rect = canvas.getBoundingClientRect();
    const width = Math.max(220, rect.width), height = Math.max(180, rect.height);
    const ratio = Math.min(2, window.devicePixelRatio || 1);
    canvas.width = Math.round(width * ratio); canvas.height = Math.round(height * ratio);
    const ctx = canvas.getContext("2d"); if (!ctx) return;
    ctx.scale(ratio, ratio); ctx.clearRect(0, 0, width, height);
    const left = 42, right = 12, top = 18, bottom = 42;
    const cw = width-left-right, ch = height-top-bottom;
    const max = Math.max(4, ...raw.map(p => num(p.attempts)));
    const ceiling = Math.max(4, Math.ceil(max / 4)*4);
    ctx.font = "11px Inter, system-ui, sans-serif";
    for (let k=0;k<=4;k++) {
      const y = top + ch*k/4;
      ctx.strokeStyle="#e9f0ef";ctx.lineWidth=1;
      ctx.beginPath();ctx.moveTo(left,y);ctx.lineTo(width-right,y);ctx.stroke();
      ctx.textAlign="right";ctx.fillStyle="#6c848c";ctx.fillText(String(Math.round(ceiling*(4-k)/4)),left-9,y+4);
    }
    const step = cw/Math.max(1,raw.length), bar = Math.max(2,Math.min(step*.57,32));
    raw.forEach((p,i)=>{
      const success=num(p.httpOk), failed=num(p.errors);
      let y=top+ch;
      for(const [value,color] of [[success,"#128f85"],[failed,"#e67e6b"]]) {
        const h = Math.min(ch,value/ceiling*ch); y-=h;
        ctx.fillStyle=color;ctx.fillRect(left+(i+.5)*step-bar/2,y,bar,h);
      }
    });
    const stride = Math.max(1,Math.ceil(raw.length/7));
    ctx.textAlign="center";ctx.fillStyle="#65818b";
    raw.forEach((p,i)=>{
      if (i%stride !== 0 && i !== raw.length-1) return;
      const iso=String(p.date||""), bits=iso.substring(0,10).split("-");
      const label=bits.length===3 ? (unit==="month"?`${bits[1]}/${bits[0].slice(-2)}`:`${bits[2]}/${bits[1]}`):iso;
      ctx.fillText(label,left+(i+.5)*step, height-15);
    });
  }
  const observer=typeof ResizeObserver!=="undefined"?new ResizeObserver(entries=>{
    for(const e of entries) { const item=known.get(e.target);if(item)graph(e.target,item.data,item.unit); }
  }):null;
  function draw(canvas,values,unit) {
    if (!(canvas instanceof HTMLCanvasElement)||!Array.isArray(values))return;
    const rows=values.map(p=>({date:p.date??p.Date,attempts:num(p.attempts??p.Attempts),
      httpOk:num(p.httpOk??p.HttpOk),errors:num(p.errors??p.Errors)}));
    known.set(canvas,{data:rows,unit});graph(canvas,rows,unit);observer?.observe(canvas);
  }
  window.scGeminiTelemetry=Object.freeze({draw});
})();
