// Paste this entire file into the browser console while signed in on /Account/Profile.
// It prints only status codes, validation result codes, and temporary upstream addresses.
void (async () => {
  const root = document.querySelector("[data-profile-media]");
  if (!(root instanceof HTMLElement)) {
    throw new Error("Open /Account/Profile before running this probe.");
  }

  const targetId = Number(root.dataset.profileUserId);
  const apiBase = root.dataset.mediaApiBase ?? "/api/media";
  if (!Number.isSafeInteger(targetId) || targetId <= 0) {
    throw new Error("The signed-in profile identifier is unavailable.");
  }

  const antiforgeryResponse = await fetch(`${apiBase}/antiforgery`, {
    credentials: "same-origin",
    cache: "no-store",
    headers: { Accept: "application/json" },
  });
  if (!antiforgeryResponse.ok) {
    throw new Error(`Antiforgery request failed: HTTP ${antiforgeryResponse.status}.`);
  }
  const antiforgery = await antiforgeryResponse.json();
  if (!antiforgery.headerName || !antiforgery.requestToken) {
    throw new Error("The antiforgery response is incomplete.");
  }

  const headers = new Headers({ Accept: "application/json" });
  headers.set(antiforgery.headerName, antiforgery.requestToken);

  const send = async (url, method, body) => {
    const requestHeaders = new Headers(headers);
    const request = {
      method,
      headers: requestHeaders,
      credentials: "same-origin",
      cache: "no-store",
    };
    if (body !== undefined) {
      requestHeaders.set("Content-Type", "application/json");
      request.body = JSON.stringify(body);
    }
    return fetch(url, request);
  };

  let mediaId = null;
  const results = [];
  try {
    const reservation = await send(`${apiBase}/reservations`, "POST", {
      targetKind: "Profile",
      targetId,
      attachmentKind: "ProfileImage",
      contentType: "image/png",
      contentLength: 67,
    });
    const reservationBody = await reservation.json();
    if (reservation.status !== 201 || !reservationBody.mediaId) {
      throw new Error(
        `Reservation failed: HTTP ${reservation.status} ${reservationBody.code ?? "unknown"}.`,
      );
    }
    mediaId = reservationBody.mediaId;

    for (let attempt = 1; attempt <= 21; attempt += 1) {
      const response = await send(
        `${apiBase}/${encodeURIComponent(mediaId)}/upload-attempts`,
        "POST",
      );
      let code = "issued";
      if (response.status !== 201) {
        const body = await response.json();
        code = body.code ?? "unknown";
      }
      results.push({
        attempt,
        status: response.status,
        upstream: response.headers.get("X-IndieFi-Upstream") ?? "missing",
        code,
      });
    }

    console.table(results);
    const allowed = results.filter((item) => item.status === 201).length;
    const rejected = results.filter(
      (item) => item.status === 429
        && item.code === "upload_short_window_limit_reached",
    ).length;
    const upstreams = [...new Set(results.map((item) => item.upstream))].sort();
    const passed = allowed === 20
      && rejected === 1
      && results.at(-1)?.status === 429
      && upstreams.includes("127.0.0.1:5101")
      && upstreams.includes("127.0.0.1:5102");
    console.log("SHARED_UPLOAD_LIMIT_RESULT", {
      passed,
      allowed,
      rejected,
      upstreams,
    });
  } finally {
    if (mediaId !== null) {
      const cleanup = await send(
        `${apiBase}/${encodeURIComponent(mediaId)}`,
        "DELETE",
      );
      console.log("SHARED_UPLOAD_LIMIT_CLEANUP", {
        status: cleanup.status,
        upstream: cleanup.headers.get("X-IndieFi-Upstream") ?? "missing",
      });
    }
  }
})();
