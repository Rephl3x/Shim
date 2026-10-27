# Shim support report receiver

This is a Cloudflare Worker with a private R2 bucket. It receives reports at `https://dev.amslabs.net/v1/reports`, stores them privately for 30 days, and never exposes a public report listing or download route. Cloudflare provisions the custom-domain DNS record and TLS certificate as part of the Worker deployment.

The private admin portal is at `https://dev.amslabs.net/admin`. Cloudflare Access protects the `/admin*` path and allows only the Shim maintainer's email identity using one-time PIN sign-in. The Worker independently verifies the Access JWT and its application audience before serving the portal, listing reports, opening a report, or downloading a ZIP. Select **Open report** to see the system inventory and every included log in its own section on the same page. The ZIP remains available for offline review. The upload and health routes remain separate from the admin path.

## Data and protections

- Uploads require an explicit user confirmation in Shim and are limited to 8 MB.
- The receiver accepts only a ZIP containing allow-listed Shim support report files, checks archive structure and expanded size, and stores the opaque archive. The authenticated report view expands it in memory, checks each file's size and checksum, then displays its text without writing extracted files to storage.
- Reports are stored in a private R2 bucket. The Worker has no unauthenticated report listing, viewing, or download endpoint; all admin routes require Cloudflare Access and Worker-side JWT validation. An authenticated administrator can also use the Cloudflare dashboard or R2 API.
- Reports expire at 30 days; a daily Worker cron deletes expired objects within 24 hours. R2 free usage includes 10 GB-month storage, 1 million Class A requests and 10 million Class B requests each month; usage beyond included quotas may be billed.
- The Worker applies a five-request-per-minute approximate per-IP rate limit. Client IPs are used only as an ephemeral rate-limit key and are not written into the report or app logs. Cloudflare request logs may contain normal request metadata.
- Worker logs record successful report IDs and size, plus validation/storage error codes; they never log report contents or IP addresses.
- Reports contain Shim/Windows versions, CPU model/core counts, installed memory, graphics adapters and driver versions, racing wheel/controller names and capabilities, matching wheel/controller driver details, a limited Shim settings summary (FFB enabled/strength, selected wheel name and device count), bridge state and recent FFB counters, and up to three recent Activity logs plus startup/watchdog diagnostics. They exclude button mappings, calibration, Forza account data, and unrelated files. Shim does not intentionally collect Windows account names or hardware serial numbers. Device names and logs can identify equipment or contain diagnostic details; Shim creates a local ZIP first so the user can inspect it.
- Upload is optional and requires an explicit user action after local creation. Reports are transmitted over HTTPS, remain private to the Shim maintainer in R2, are used for troubleshooting, and expire after 30 days with daily cleanup within 24 hours. Reports are not sold, published, or shared with other people. Cloudflare provides the network and private storage service; normal network request metadata may be processed by Cloudflare. Shim does not email reports. Its email-copy option creates a local draft with the report attached; the user reviews and sends it through their own mail provider if desired.

## Deploy

Install the pinned development dependency with `npm ci`, then run `npm run check` and `npm run deploy`. The Worker binds R2 and rate limiting and attaches to the custom domain in `wrangler.jsonc`. Deployment provisions the R2 bucket if required and adds the `dev.amslabs.net` DNS record/certificate.

To read a report, open the private admin portal and select **Open report**. Each file appears in its own section; the system inventory also has a quick summary. Download the ZIP from the same page if you need the original archive. Reports expire automatically; delete a report early from the dashboard when requested.
