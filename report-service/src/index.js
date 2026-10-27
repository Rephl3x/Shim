import { ADMIN_HTML } from "./admin-page.js";
import { MAX_UPLOAD_BYTES, validateZip, openReport } from "./report-archive.js";

const REPORT_RETENTION_SECONDS = 30 * 24 * 60 * 60;
const ACCESS_ISSUER = "https://ams-labs.cloudflareaccess.com";
let accessKeysPromise;



function json(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store", "x-content-type-options": "nosniff",
  }});
}

function adminHeaders(extra = {}) {
  return { "cache-control": "no-store", "x-content-type-options": "nosniff", "referrer-policy": "no-referrer", "x-frame-options": "DENY", "content-security-policy": "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'", ...extra };
}

function unauthorized() { return json({ error: "unauthorized" }, 401); }

async function getAccessKeys(refresh = false) {
  if (refresh) accessKeysPromise = undefined;
  if (!accessKeysPromise) accessKeysPromise = fetch(ACCESS_ISSUER + "/cdn-cgi/access/certs", { cf: { cacheTtl: 300, cacheEverything: true } })
    .then(response => { if (!response.ok) throw new Error("access_keys_unavailable"); return response.json(); })
    .catch(error => { accessKeysPromise = undefined; throw error; });
  return accessKeysPromise;
}

function decodePart(part) {
  const normalized = part.replace(/-/g, "+").replace(/_/g, "/");
  return JSON.parse(atob(normalized + "=".repeat((4 - normalized.length % 4) % 4)));
}

async function verifyAccess(request, env) {
  if (!env.ACCESS_AUD) return false;
  const token = request.headers.get("cf-access-jwt-assertion");
  if (!token || token.length > 12000) return false;
  const parts = token.split(".");
  if (parts.length !== 3) return false;
  try {
    const header = decodePart(parts[0]), claims = decodePart(parts[1]);
    if (header.alg !== "RS256" || !header.kid || claims.iss !== ACCESS_ISSUER) return false;
    const aud = Array.isArray(claims.aud) ? claims.aud : [claims.aud];
    const now = Math.floor(Date.now() / 1000);
    if (!aud.includes(env.ACCESS_AUD) || !Number.isFinite(claims.exp) || claims.exp < now ||
        (claims.nbf != null && (!Number.isFinite(claims.nbf) || claims.nbf > now + 30))) return false;
    let keys = await getAccessKeys();
    let jwk = (keys.keys || []).find(key => key.kid === header.kid && key.kty === "RSA");
    if (!jwk) {
      keys = await getAccessKeys(true);
      jwk = (keys.keys || []).find(key => key.kid === header.kid && key.kty === "RSA");
    }
    if (!jwk) return false;
    const publicKey = await crypto.subtle.importKey("jwk", jwk, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["verify"]);
    const signature = Uint8Array.from(atob(parts[2].replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - parts[2].length % 4) % 4)), ch => ch.charCodeAt(0));
    return await crypto.subtle.verify("RSASSA-PKCS1-v1_5", publicKey, signature, new TextEncoder().encode(parts[0] + "." + parts[1]));
  } catch { return false; }
}

async function handleAdmin(request, env, url) {
  if (!await verifyAccess(request, env)) return unauthorized();
  if (request.method === "GET" && (url.pathname === "/admin" || url.pathname === "/admin/"))
    return new Response(ADMIN_HTML, { headers: adminHeaders({ "content-type": "text/html; charset=utf-8" }) });
  if (request.method === "GET" && url.pathname === "/admin/api/reports") {
    const cursor = url.searchParams.get("cursor") || undefined;
    if (cursor && cursor.length > 2048) return json({ error: "invalid_cursor" }, 400);
    try {
      const result = await env.SUPPORT_REPORTS.list({ prefix: "reports/", cursor, limit: 100 });
      const reports = result.objects.map(object => ({
        reportId: object.customMetadata && object.customMetadata.reportId || object.key.slice("reports/".length, -".zip".length),
        uploaded: object.uploaded.toISOString(), size: object.size,
      }));
      return json({ reports, cursor: result.truncated ? result.cursor : null });
    } catch (error) {
      console.error(JSON.stringify({ event: "report_admin_list_failed", error: String(error.message || "storage_error") }));
      return json({ error: "report_list_failed" }, 503);
    }
  }
  const detail = url.pathname.match(/^\/admin\/api\/reports\/([A-F0-9]{12})$/);
  if (request.method === "GET" && detail) {
    const reportId = detail[1];
    try {
      const object = await env.SUPPORT_REPORTS.get("reports/" + reportId + ".zip");
      if (!object) return json({ error: "not_found" }, 404);
      if (object.size > MAX_UPLOAD_BYTES) return json({ error: "invalid_report_size" }, 422);
      const files = await openReport(await object.arrayBuffer());
      console.info(JSON.stringify({ event: "report_opened", reportId, fileCount: files.length }));
      return json({ reportId, uploaded: object.uploaded.toISOString(), size: object.size, files });
    } catch (error) {
      console.error(JSON.stringify({ event: "report_admin_open_failed", reportId, error: String(error.message || "report_error") }));
      return json({ error: "report_open_failed" }, 422);
    }
  }
  const download = url.pathname.match(/^\/admin\/api\/reports\/([A-F0-9]{12})\/download$/);
  if (request.method === "GET" && download) {
    const reportId = download[1];
    try {
      const object = await env.SUPPORT_REPORTS.get("reports/" + reportId + ".zip");
      if (!object) return json({ error: "not_found" }, 404);
      console.info(JSON.stringify({ event: "report_downloaded", reportId }));
      return new Response(object.body, { headers: adminHeaders({
        "content-type": "application/zip", "content-length": String(object.size),
        "content-disposition": "attachment; filename=\"Shim-report-" + reportId + ".zip\"",
      }) });
    } catch (error) {
      console.error(JSON.stringify({ event: "report_admin_download_failed", reportId, error: String(error.message || "storage_error") }));
      return json({ error: "report_download_failed" }, 503);
    }
  }
  return json({ error: "not_found" }, 404);
}

async function pruneExpired(bucket, now) {
  const cutoff = now - REPORT_RETENTION_SECONDS * 1000;
  let cursor;
  do {
    const page = await bucket.list({ prefix: "reports/", cursor, limit: 1000 });
    const expired = page.objects.filter(object => object.uploaded.getTime() < cutoff).map(object => object.key);
    if (expired.length) await bucket.delete(expired);
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/admin" || url.pathname === "/admin/" || url.pathname.startsWith("/admin/api/"))
      return handleAdmin(request, env, url);
    if (request.method === "GET" && url.pathname === "/healthz") return json({ ok: true });
    if (request.method !== "POST" || url.pathname !== "/v1/reports") return json({ error: "not_found" }, 404);

    const source = request.headers.get("cf-connecting-ip") || "unknown";
    const rate = await env.REPORT_LIMIT.limit({ key: source });
    if (!rate.success) return json({ error: "rate_limited" }, 429);
    if ((request.headers.get("content-type") || "").split(";")[0].trim().toLowerCase() !== "application/zip")
      return json({ error: "application_zip_required" }, 415);
    const contentLength = Number(request.headers.get("content-length"));
    if (!Number.isInteger(contentLength) || contentLength < 22 || contentLength > MAX_UPLOAD_BYTES)
      return json({ error: "report_must_be_under_8mb" }, 413);
    const body = await request.arrayBuffer();
    if (body.byteLength !== contentLength) return json({ error: "incomplete_upload" }, 400);
    try { validateZip(body); }
    catch (error) {
      console.warn(JSON.stringify({ event: "invalid_report_bundle", code: String(error.message || "invalid_bundle") }));
      return json({ error: "invalid_support_bundle" }, 400);
    }

    const reportId = crypto.randomUUID().replace(/-/g, "").slice(0, 12).toUpperCase();
    try {
      await env.SUPPORT_REPORTS.put("reports/" + reportId + ".zip", body, {
        httpMetadata: { contentType: "application/zip", cacheControl: "private, no-store" },
        customMetadata: { reportId, receivedUtc: new Date().toISOString() },
      });
      console.info(JSON.stringify({ event: "report_received", reportId, sizeBytes: body.byteLength }));
      return json({ reportId, retentionDays: 30 }, 201);
    } catch (error) {
      console.error(JSON.stringify({ event: "report_store_failed", reportId, error: String(error.message || "storage_error") }));
      return json({ error: "storage_failed" }, 503);
    }
  },

  async scheduled(controller, env, context) {
    context.waitUntil(pruneExpired(env.SUPPORT_REPORTS, Date.now()));
  },
};
