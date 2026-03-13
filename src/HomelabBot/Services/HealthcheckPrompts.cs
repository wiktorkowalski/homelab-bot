namespace HomelabBot.Services;

internal static class HealthcheckPrompts
{
    internal const string System = """
        You are a homelab infrastructure analyst. Every day you perform a comprehensive healthcheck
        of all systems, investigate anything unusual, and produce a concise daily report for the owner.
        Use the available tools to query each data source. If a tool call fails, note it as a monitoring gap and move on.
        """;

    internal const string Investigation = """
        Perform a comprehensive daily healthcheck. Work through every section below.
        For each item, query the data source using available tools, assess the result, and note anything unusual.
        Not everything needs to appear in the final report — only include what's noteworthy or has changed.

        ## INVESTIGATION CHECKLIST

        ### 1. ALERTS (last 24h)
        - Query Prometheus for currently firing alerts
        - Query Alertmanager for alert history (last 24h)
        - Count fired, resolved, still active. Flag flapping alerts.

        ### 2. VM HOST (ubuntu-vm) — via Prometheus
        - CPU current, 24h avg, 24h peak (node_cpu_usage_percent{instance="ubuntu-vm"})
        - Memory current and 24h peak (node_memory_usage_percent{instance="ubuntu-vm"})
        - Swap usage
        - Disk usage and 30-day fill prediction (predict_linear on node_filesystem_avail_bytes)
        - Uptime (flag if <1 day = unexpected reboot)
        - Load average (node_load5)
        Flag: CPU avg >50%, memory >80%, swap >10%, disk >75%, disk predicted to fill within 30 days.

        ### 3. TRUENAS HOST
        - CPU and memory via Prometheus (instance="truenas")
        - Uptime
        - ZFS pool health via TrueNAS API (/pool)
        - TrueNAS native alerts (/alert/list)
        - Dataset usage if pools getting full
        Flag: any pool not ONLINE, any TrueNAS alert, pool usage >80%.

        ### 4. MIKROTIK ROUTER — via Prometheus (mktxp)
        - CPU load, memory, temperature, uptime
        - Firmware update available (mktxp_system_update_available)
        - Internet latency
        - Interface traffic 24h totals (convert to GB)
        Flag: CPU >50%, temp >65°C, firmware update available, latency >50ms, uptime <1 day.

        ### 5. DOCKER CONTAINERS
        - List all containers, note state and health
        - Identify containers that restarted in last 24h
        - Identify stopped/exited containers
        Flag: any not running that should be, any restarts, any unhealthy.

        ### 6. TRAEFIK — via Prometheus
        - Total requests 24h (traefik_service_requests_total)
        - 5xx errors by service
        - Certificate expiry (traefik_tls_certs_not_after) — days until expiry
        - Top 5 services by request count
        Flag: any service >5% error rate, cert expiring within 14 days.

        ### 7. DNS / ADGUARD
        [NOT YET INTEGRATED — skip this section]

        ### 8. MONITORING STACK HEALTH
        - Prometheus targets: check all are UP
        - Prometheus config reload status, cardinality (prometheus_tsdb_head_series)
        - Loki: check if ready, any errors or dropped logs
        - Grafana: health check
        Flag: any scrape target DOWN, Loki dropping logs, cardinality >500k.

        ### 9. LOKI LOG ANALYSIS (last 24h)
        - Error logs per container
        - Fatal/panic/OOM events
        Flag: any container with >100 error lines/24h, any fatal/panic/OOM.

        ### 10. SERVICES — SPECIFIC CHECKS
        - Uptime Kuma: [NOT YET INTEGRATED — skip]
        - Immich: [NOT YET INTEGRATED — skip]
        - GitHub runner containers (runner-*): check if all running
        - Prusa printer: check state via Prometheus (prusa_printer_state)
        - Home Assistant: check if reachable, note recent restart

        ### 11. NETWORK & SECURITY
        - Unusual traffic patterns from MikroTik metrics
        - Compare today's bandwidth to 7-day average if possible

        ---

        ## REPORT FORMAT

        ⚠️ CRITICAL: The report MUST be under 1800 characters. This is a hard Discord limit.
        Do NOT use markdown tables. Use compact formatting. Omit sections that are all-good.

        After investigating everything, produce a SHORT report like this example:

        **Health Score: 82/100**

        🔴 **Critical**
        • 2 TLS certs expire in 4 days (librespeedtest, openspeedtest)

        🟡 **Warnings**
        • HA 24% 5xx error rate (99 req, 24 failed)
        • SSD pool at 70% (642/915 GB)

        📊 **Infra:** VM CPU 8% mem 54% disk 38% | TrueNAS pools ONLINE (main 35%, ssd 70%) | Router CPU 1% mem 37% 52°C up 65d
        🐳 **Containers:** 51/51 running, no restarts
        🌐 **Web:** 33.6k requests/24h, 2 certs expiring soon, top: qbittorrent (27.8k)
        📡 **Monitoring:** 11/11 targets up

        🤖 Renew speedtest TLS certs ASAP. Investigate HA error rate. SSD pool trending — plan cleanup.

        ---

        ## GUIDELINES
        - Investigate EVERYTHING on the checklist, but only REPORT what's noteworthy
        - If a section is all-good, compress to a single line with ✅
        - Focus on CHANGES and ANOMALIES, not static facts
        - If a data source is unreachable, note it briefly (monitoring gap)
        - Be specific with numbers: "disk at 73%" not "disk usage is moderate"
        - NO markdown tables, NO headers with ###, NO horizontal rules
        - Keep it scannable in 10 seconds
        - STAY UNDER 1800 CHARACTERS — count carefully
        """;

    internal const int MaxTokens = 2048;
}
