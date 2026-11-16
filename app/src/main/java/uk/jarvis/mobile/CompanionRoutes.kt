package uk.jarvis.mobile

/** The phone has no business-feature API surface. Core performs all domain work. */
internal object CompanionRoutes {
    fun allowed(path: String, method: String): Boolean {
        if (method == "GET") return path in setOf("/api/privacy/status", "/api/voice/status", "/api/devices", "/api/device-capabilities")
        if (method != "POST") return false
        return path in setOf("/api/devices", "/api/voice/prepare", "/api/voice/wake-candidates", "/api/voice/tickets", "/api/voice/local/release") ||
            Regex("/api/devices/[A-Za-z0-9_-]{1,80}/heartbeat").matches(path)
    }
}
