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
const notificationLiveState = document.querySelector<HTMLElement>(
  "[data-notification-live-state]",
);
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
connection.onreconnecting(() => {
  setLiveState("실시간 연결을 복구하고 있습니다.", "warning");
});
connection.onreconnected(() => {
  setLiveState("연결되었습니다. 최신 알림 확인 중…", "loading");
  void refreshSnapshot();
});
connection.onclose(() => {
  setLiveState("연결이 끊겨 주기적으로 새 알림을 확인합니다.", "warning");
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
    setLiveState("실시간 연결을 기다리며 주기적으로 확인합니다.", "warning");
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
    setLiveState("최신 알림을 불러오지 못했습니다. 자동으로 다시 시도합니다.", "warning");
    return;
  }

  if (response.redirected || response.status === 401 || response.status === 403) {
    authenticationLost = true;
    setLiveState("로그인 상태가 만료되었습니다. 페이지를 새로고침해 주세요.", "warning");
    await connection.stop();
    return;
  }

  if (!response.ok || !response.headers.get("content-type")?.includes("application/json")) {
    setLiveState("최신 알림을 확인하지 못했습니다. 자동으로 다시 시도합니다.", "warning");
    return;
  }

  const value: unknown = await response.json();
  if (!isNotificationSnapshot(value)) {
    return;
  }

  updateUnreadCount(value.unreadCount);
  setLiveState("최신 알림을 자동으로 확인하고 있습니다.", "success");
  if (isNotificationPage && value.latestNotificationId !== displayedLatestId) {
    window.location.reload();
    return;
  }

  displayedLatestId = value.latestNotificationId;
}

function setLiveState(
  message: string,
  kind: "loading" | "success" | "warning",
): void {
  if (notificationLiveState === null) {
    return;
  }

  notificationLiveState.textContent = message;
  notificationLiveState.classList.remove(
    "dynamic-status-loading",
    "dynamic-status-success",
    "dynamic-status-warning",
  );
  notificationLiveState.classList.add(`dynamic-status-${kind}`);
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
