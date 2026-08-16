import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";

type NotificationSnapshot = {
  unreadCount: number;
  latestNotificationId: string | null;
};

const pollIntervalMilliseconds = 15_000;
const badge = document.querySelector<HTMLElement>("[data-notification-badge]");
const pageUnreadCount = document.querySelector<HTMLElement>("[data-notification-unread-count]");
const notificationList = document.querySelector<HTMLElement>("[data-notification-list]");
const isNotificationPage = pageUnreadCount !== null;
const initialLatestId = notificationList
  ?.querySelector<HTMLElement>("[data-notification-id]")
  ?.dataset.notificationId ?? null;
let displayedLatestId = initialLatestId;
let authenticationLost = false;
let refreshPromise: Promise<void> | null = null;
let reconnectTimer: number | null = null;

const connection = new HubConnectionBuilder()
  .withUrl("/hubs/notifications")
  .withAutomaticReconnect([0, 2_000, 5_000, 10_000])
  .configureLogging(LogLevel.Warning)
  .build();

connection.on("NotificationsChanged", () => {
  void refreshSnapshot();
});
connection.onreconnected(() => {
  void refreshSnapshot();
});
connection.onclose(() => {
  scheduleReconnect();
});

void startConnection();
void refreshSnapshot();

window.setInterval(() => {
  if (!authenticationLost && connection.state !== HubConnectionState.Connected) {
    void refreshSnapshot();
    void startConnection();
  }
}, pollIntervalMilliseconds);

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible" && !authenticationLost) {
    void refreshSnapshot();
    void startConnection();
  }
});

async function startConnection(): Promise<void> {
  if (
    authenticationLost
    || connection.state !== HubConnectionState.Disconnected
  ) {
    return;
  }

  if (reconnectTimer !== null) {
    window.clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }

  try {
    await connection.start();
    await refreshSnapshot();
  } catch {
    scheduleReconnect();
  }
}

function scheduleReconnect(): void {
  if (authenticationLost || reconnectTimer !== null) {
    return;
  }

  reconnectTimer = window.setTimeout(() => {
    reconnectTimer = null;
    void startConnection();
  }, pollIntervalMilliseconds);
}

function refreshSnapshot(): Promise<void> {
  if (refreshPromise !== null || authenticationLost) {
    return refreshPromise ?? Promise.resolve();
  }

  refreshPromise = fetchSnapshot().finally(() => {
    refreshPromise = null;
  });
  return refreshPromise;
}

async function fetchSnapshot(): Promise<void> {
  let response: Response;
  try {
    response = await fetch("/Notifications?handler=Snapshot", {
      cache: "no-store",
      credentials: "same-origin",
      headers: { Accept: "application/json" },
    });
  } catch {
    return;
  }

  if (response.redirected || response.status === 401 || response.status === 403) {
    authenticationLost = true;
    await connection.stop();
    return;
  }

  if (!response.ok || !response.headers.get("content-type")?.includes("application/json")) {
    return;
  }

  const value: unknown = await response.json();
  if (!isNotificationSnapshot(value)) {
    return;
  }

  updateUnreadCount(value.unreadCount);
  if (isNotificationPage && value.latestNotificationId !== displayedLatestId) {
    window.location.reload();
    return;
  }

  displayedLatestId = value.latestNotificationId;
}

function updateUnreadCount(unreadCount: number): void {
  const text = unreadCount > 99 ? "99+" : String(unreadCount);
  if (badge !== null) {
    badge.textContent = text;
    badge.classList.toggle("hidden", unreadCount === 0);
    badge.setAttribute("aria-label", `읽지 않은 알림 ${unreadCount}개`);
  }

  if (pageUnreadCount !== null) {
    pageUnreadCount.textContent = String(unreadCount);
  }
}

function isNotificationSnapshot(value: unknown): value is NotificationSnapshot {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const snapshot = value as Record<string, unknown>;
  return Number.isSafeInteger(snapshot.unreadCount)
    && (snapshot.unreadCount as number) >= 0
    && (snapshot.latestNotificationId === null
      || (typeof snapshot.latestNotificationId === "string"
        && /^\d+$/.test(snapshot.latestNotificationId)));
}
