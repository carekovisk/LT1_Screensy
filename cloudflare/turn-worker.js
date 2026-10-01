// Cloudflare Worker: hands out short-lived Cloudflare TURN credentials to the LT1 page.
// The API token stays here as a secret; browsers only ever see credentials that expire.
//
// Settings (Worker -> Settings -> Variables and Secrets):
//   TURN_KEY_ID        (Text)   - "Turn Token ID" from Realtime -> TURN Server
//   TURN_KEY_API_TOKEN (Secret) - "API Token" from the same key

// Pages allowed to ask for credentials: GitHub Pages (viewers) and the exe (broadcaster).
const ALLOWED_ORIGINS = ['https://carekovisk.github.io', 'https://lt1.stream'];

// Longer than any stream: TURN allocations stop refreshing once credentials expire.
const CREDENTIAL_TTL_SECONDS = 12 * 60 * 60;

export default {
  async fetch(request, env) {
    const origin = request.headers.get('Origin') || '';
    const allowed = ALLOWED_ORIGINS.includes(origin);
    const cors = allowed ? { 'Access-Control-Allow-Origin': origin, 'Vary': 'Origin' } : {};

    if (request.method === 'OPTIONS')
      return new Response(null, { status: allowed ? 204 : 403, headers: { ...cors, 'Access-Control-Allow-Methods': 'GET' } });
    if (!allowed)
      return new Response('Forbidden', { status: 403 });
    if (request.method !== 'GET')
      return new Response('Method Not Allowed', { status: 405, headers: cors });

    const upstream = await fetch(
      `https://rtc.live.cloudflare.com/v1/turn/keys/${env.TURN_KEY_ID}/credentials/generate-ice-servers`,
      {
        method: 'POST',
        headers: { 'Authorization': `Bearer ${env.TURN_KEY_API_TOKEN}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ ttl: CREDENTIAL_TTL_SECONDS })
      }
    );
    if (!upstream.ok)
      return new Response('TURN credentials unavailable', { status: 502, headers: cors });

    const { iceServers } = await upstream.json();

    // Browsers block port 53 and the TURN URL would just time out, so drop it.
    const cleaned = (iceServers || []).map(server => ({
      ...server,
      urls: [].concat(server.urls).filter(url => !/:53(\?|$)/.test(url))
    })).filter(server => server.urls.length > 0);

    return Response.json({ iceServers: cleaned }, { headers: { ...cors, 'Cache-Control': 'no-store' } });
  }
};
