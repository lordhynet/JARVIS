package uk.jarvis.mobile

import uk.jarvis.mobile.contracts.CoreCompanionRoutes

/** The phone has no business-feature API surface. Core performs all domain work. */
internal object CompanionRoutes {
    fun allowed(path: String, method: String): Boolean {
        if (method == "GET") return path in CoreCompanionRoutes.getPaths
        if (method != "POST") return false
        return path in CoreCompanionRoutes.postPaths || CoreCompanionRoutes.heartbeatPattern.matches(path)
    }
}
