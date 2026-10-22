const dateFmt = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "medium" });
const timeFmt = new Intl.DateTimeFormat("pt-BR", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const dayFmt = new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "short" });

export function fmtDate(value) {
  if (!value) return "";
  const d = new Date(value);
  return isNaN(d) ? "" : dateFmt.format(d);
}

export function fmtTime(value) {
  const d = new Date(value);
  return isNaN(d) ? "" : timeFmt.format(d);
}

export function fmtShort(value) {
  const d = new Date(value);
  if (isNaN(d)) return "";
  return d.toDateString() === new Date().toDateString() ? timeFmt.format(d) : dayFmt.format(d) + " " + timeFmt.format(d).slice(0, 5);
}

export function fmtSize(n) {
  if (n < 1024) return n + " B";
  if (n < 1024 * 1024) return (n / 1024).toFixed(1).replace(".", ",") + " KB";
  return (n / 1024 / 1024).toFixed(1).replace(".", ",") + " MB";
}

export function displayAddress(text) {
  if (!text) return "(sem remetente)";
  const first = text.split(/,(?=(?:[^"]*"[^"]*")*[^"]*$)/)[0].trim();
  const m = first.match(/^"?([^"<]*?)"?\s*<([^>]+)>$/);
  return m ? (m[1].trim() || m[2]) : first;
}

export function isSeen(message) {
  return (message?.flags || []).some((f) => f.toLowerCase() === "\\seen");
}

export function plural(count, one, many) {
  return `${count} ${count === 1 ? one : many}`;
}

export function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}
