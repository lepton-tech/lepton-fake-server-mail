export async function api(path, options = {}) {
  const opts = { ...options, headers: { ...(options.headers || {}) } };
  if (opts.json !== undefined) {
    opts.body = JSON.stringify(opts.json);
    opts.headers["Content-Type"] = "application/json";
    delete opts.json;
  }
  const res = await fetch(path, opts);
  if (!res.ok) {
    let message = res.status + " " + res.statusText;
    try {
      const body = await res.json();
      message = body.detail || body.title || message;
    } catch { }
    throw new Error(message);
  }
  if (res.status === 204) return null;
  const type = res.headers.get("content-type") || "";
  return type.includes("json") ? res.json() : res.text();
}

export const encode = encodeURIComponent;
