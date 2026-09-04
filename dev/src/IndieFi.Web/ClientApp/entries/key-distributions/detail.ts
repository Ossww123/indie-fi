import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { getRequiredElement } from "../../shared/dom";

interface WaitingRoomSnapshot {
  dropId: number;
  dropStatus: string;
  remainingKeys: number;
  participationOutcome: string;
  entryState: string | null;
  position: number | null;
  aheadCount: number | null;
  waitingCount: number;
  preWaitingCount: number;
  admittedCount: number;
  admissionExpiresUnixTimeMilliseconds: number | null;
}

interface WaitingRoomChangedMessage {
  dropId: number;
}

const pollingIntervalMilliseconds = 15_000;
const reconnectDelayMilliseconds = 15_000;
const refreshDebounceMilliseconds = 100;
const root = getRequiredElement<HTMLElement>("[data-waiting-room-client]");
const stateElement = getRequiredElement<HTMLElement>("[data-live-state]", root);
const positionElement = getRequiredElement<HTMLElement>("[data-live-position]", root);
const countsElement = getRequiredElement<HTMLElement>("[data-live-counts]", root);
const admissionElement = getRequiredElement<HTMLElement>("[data-live-admission]", root);
const inventoryElement = getRequiredElement<HTMLElement>("[data-key-drop-inventory]");
const claimButton = document.querySelector<HTMLButtonElement>("[data-waiting-room-claim]");
const dropId = Number(root.dataset.dropId);
const totalKeys = Number(root.dataset.totalKeys);
const snapshotUrl = requireEndpoint(root.dataset.snapshotUrl);
const hubUrl = requireEndpoint(root.dataset.hubUrl);

if (!Number.isSafeInteger(dropId) || dropId <= 0
  || !Number.isSafeInteger(totalKeys) || totalKeys < 0) {
  throw new Error("The waiting-room client configuration is invalid.");
}

const connection = new HubConnectionBuilder()
  .withUrl(hubUrl)
  .withAutomaticReconnect([0, 2_000, 5_000, 10_000])
  .configureLogging(LogLevel.Warning)
  .build();

let pollingId: number | undefined;
let reconnectId: number | undefined;
let refreshId: number | undefined;
let refreshInFlight: Promise<void> | undefined;
let latestSnapshot: WaitingRoomSnapshot | undefined;
let expiryRefreshScheduled = false;
let authenticationLost = false;

connection.on("WaitingRoomDropChanged", handleChanged);
connection.on("WaitingRoomUserChanged", handleChanged);
connection.onreconnecting(() => {
  setClientState(
    "reconnecting",
    "실시간 연결을 복구하고 있습니다. 그동안 주기적으로 상태를 확인합니다.",
    "warning",
  );
  startPolling();
});
connection.onreconnected(async () => {
  setClientState("connected", "연결되었습니다. 최신 상태 확인 중…", "loading");
  stopPolling();
  await subscribeAndRefresh();
});
connection.onclose(() => {
  if (authenticationLost) {
    return;
  }

  setClientState(
    "polling",
    "실시간 연결이 끊겨 주기적으로 상태를 확인하고 있습니다.",
    "warning",
  );
  startPolling();
  scheduleConnectionStart();
});

window.setInterval(updateAdmissionCountdown, 1_000);
void refreshSnapshot();
void startConnection();

function handleChanged(message: WaitingRoomChangedMessage): void {
  if (message.dropId === dropId) {
    scheduleRefresh();
  }
}

async function startConnection(): Promise<void> {
  if (authenticationLost || connection.state !== HubConnectionState.Disconnected) {
    return;
  }

  try {
    await connection.start();
    setClientState("connected", "연결되었습니다. 최신 상태 확인 중…", "loading");
    stopPolling();
    await subscribeAndRefresh();
  } catch {
    setClientState(
      "polling",
      "실시간 연결을 기다리며 주기적으로 상태를 확인하고 있습니다.",
      "warning",
    );
    startPolling();
    scheduleConnectionStart();
  }
}

async function subscribeAndRefresh(): Promise<void> {
  try {
    await connection.invoke("Subscribe", dropId);
  } catch {
    setClientState(
      "polling",
      "실시간 구독을 복구하는 동안 주기적으로 상태를 확인합니다.",
      "warning",
    );
    startPolling();
  } finally {
    await refreshSnapshot();
  }
}

function scheduleConnectionStart(): void {
  if (authenticationLost || reconnectId !== undefined) {
    return;
  }

  reconnectId = window.setTimeout(() => {
    reconnectId = undefined;
    void startConnection();
  }, reconnectDelayMilliseconds);
}

function startPolling(): void {
  if (pollingId !== undefined) {
    return;
  }

  pollingId = window.setInterval(() => {
    void refreshSnapshot();
  }, pollingIntervalMilliseconds);
}

function stopPolling(): void {
  if (pollingId !== undefined) {
    window.clearInterval(pollingId);
    pollingId = undefined;
  }
}

function scheduleRefresh(): void {
  if (refreshId !== undefined) {
    window.clearTimeout(refreshId);
  }

  refreshId = window.setTimeout(() => {
    refreshId = undefined;
    void refreshSnapshot();
  }, refreshDebounceMilliseconds);
}

function refreshSnapshot(): Promise<void> {
  refreshInFlight ??= fetchSnapshot()
    .catch(() => {
      setClientState(
        "polling",
        "최신 상태를 불러오지 못했습니다. 자동으로 다시 시도합니다.",
        "warning",
      );
      startPolling();
    })
    .finally(() => {
      refreshInFlight = undefined;
    });
  return refreshInFlight;
}

async function fetchSnapshot(): Promise<void> {
  const response = await fetch(snapshotUrl, {
    credentials: "same-origin",
    headers: { Accept: "application/json" },
    cache: "no-store",
  });
  if (response.status === 401) {
    authenticationLost = true;
    stopPolling();
    setClientState(
      "unauthorized",
      "로그인 상태가 만료되었습니다. 페이지를 새로고침해 주세요.",
      "warning",
    );
    await connection.stop();
    return;
  }

  if (!response.ok) {
    setClientState(
      "polling",
      "최신 상태를 불러오지 못했습니다. 자동으로 다시 시도합니다.",
      "warning",
    );
    startPolling();
    return;
  }

  const snapshot = await response.json() as WaitingRoomSnapshot;
  if (snapshot.dropId !== dropId) {
    throw new Error("The waiting-room snapshot belongs to another drop.");
  }

  latestSnapshot = snapshot;
  expiryRefreshScheduled = false;
  renderSnapshot(snapshot);
}

function renderSnapshot(snapshot: WaitingRoomSnapshot): void {
  setClientState(
    root.dataset.clientState ?? "connected",
    stateText(snapshot),
    "success",
  );

  const hasPosition = snapshot.position !== null && snapshot.aheadCount !== null;
  positionElement.hidden = !hasPosition;
  positionElement.textContent = hasPosition
    ? `현재 ${snapshot.position}번 · 앞에 ${snapshot.aheadCount}명`
    : "";

  countsElement.hidden = false;
  countsElement.textContent = `대기 ${snapshot.waitingCount}명 · 사전 대기 ${snapshot.preWaitingCount}명 · 입장 중 ${snapshot.admittedCount}명`;
  inventoryElement.textContent = `총 ${totalKeys}개 중 ${snapshot.remainingKeys}개 남음`;

  if (claimButton !== null) {
    const admitted = snapshot.entryState === "Admitted";
    claimButton.disabled = !admitted;
    claimButton.textContent = admitted ? "키 받기" : "대기실 입장 후 키 받기";
  }

  updateAdmissionCountdown();
}

function setClientState(
  state: string,
  message: string,
  kind: "loading" | "success" | "warning",
): void {
  root.dataset.clientState = state;
  stateElement.hidden = false;
  stateElement.textContent = message;
  stateElement.classList.remove(
    "dynamic-status-loading",
    "dynamic-status-success",
    "dynamic-status-warning",
  );
  stateElement.classList.add(`dynamic-status-${kind}`);
}

function stateText(snapshot: WaitingRoomSnapshot): string {
  switch (snapshot.entryState) {
    case "PreWaiting": return "최신 상태 · 사전 대기 중";
    case "Waiting": return "최신 상태 · 대기 중";
    case "Admitted": return "최신 상태 · 입장 가능";
    case "Completed": return "최신 상태 · 지급 완료";
    case "Expired": return "최신 상태 · 입장 만료 — 다시 참여하면 마지막 순서로 이동합니다.";
    case "Terminal": return "최신 상태 · 대기실 종료";
    case null: return `최신 상태 · ${participationText(snapshot.participationOutcome)}`;
    default: return "최신 대기실 상태를 확인했습니다.";
  }
}

function participationText(outcome: string): string {
  switch (outcome) {
    case "Available": return "대기실 입장 전";
    case "Completed": return "지급 완료";
    case "NotYetOpen": return "대기실 오픈 전";
    case "NotEligible": return "참여 자격 미충족";
    case "SoldOut": return "키 소진";
    case "ClosedEarly": return "조기 마감";
    case "Terminal": return "대기실 종료";
    default: return "상태 확인 완료";
  }
}

function requireEndpoint(value: string | undefined): string {
  if (value === undefined || value.length === 0) {
    throw new Error("A waiting-room client endpoint is missing.");
  }

  return value;
}

function updateAdmissionCountdown(): void {
  const expiresAt = latestSnapshot?.admissionExpiresUnixTimeMilliseconds;
  if (latestSnapshot?.entryState !== "Admitted" || expiresAt === null || expiresAt === undefined) {
    admissionElement.hidden = true;
    admissionElement.textContent = "";
    return;
  }

  const remainingSeconds = Math.max(0, Math.ceil((expiresAt - Date.now()) / 1_000));
  admissionElement.hidden = false;
  admissionElement.textContent = `입장 가능 시간 ${remainingSeconds}초 남음`;
  if (remainingSeconds === 0 && !expiryRefreshScheduled) {
    expiryRefreshScheduled = true;
    scheduleRefresh();
  }
}
