const dateFmt = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "medium" });
const timeFmt = new Intl.DateTimeFormat("pt-BR", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const dayFmt = new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "short" });
const decimalFmt = new Intl.NumberFormat("pt-BR", { minimumFractionDigits: 1, maximumFractionDigits: 1, useGrouping: false });

const toDate = (value) => {
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
};

export function fmtDate(value) {
  const d = value ? toDate(value) : null;
  return d ? dateFmt.format(d) : "";
}

export function fmtTime(value) {
  const d = toDate(value);
  return d ? timeFmt.format(d) : "";
}

export function fmtShort(value) {
  const d = toDate(value);
  if (!d) return "";
  return d.toDateString() === new Date().toDateString() ? timeFmt.format(d) : dayFmt.format(d) + " " + timeFmt.format(d).slice(0, 5);
}

export function fmtSize(n) {
  if (n < 1024) return n + " B";
  if (n < 1024 * 1024) return decimalFmt.format(n / 1024) + " KB";
  return decimalFmt.format(n / 1024 / 1024) + " MB";
}

export function displayAddress(text) {
  if (!text) return "(sem remetente)";
  const first = text.split(/,(?=(?:[^"]*"[^"]*")*[^"]*$)/)[0].trim();
  const m = first.match(/^"?([^"<]*?)"?\s*<([^>]+)>$/);
  return m ? (m[1].trim() || m[2]) : first;
}

export function isSeen(message) {
  return (message?.flags ?? []).some((f) => f.toLowerCase() === "\\seen");
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
