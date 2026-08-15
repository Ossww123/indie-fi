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
  root.dataset.clientState = "reconnecting";
  startPolling();
});
connection.onreconnected(async () => {
  root.dataset.clientState = "connected";
  stopPolling();
  await subscribeAndRefresh();
});
connection.onclose(() => {
  if (authenticationLost) {
    return;
  }

  root.dataset.clientState = "polling";
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
    root.dataset.clientState = "connected";
    stopPolling();
    await subscribeAndRefresh();
  } catch {
    root.dataset.clientState = "polling";
    startPolling();
    scheduleConnectionStart();
  }
}

async function subscribeAndRefresh(): Promise<void> {
  try {
    await connection.invoke("Subscribe", dropId);
  } catch {
    root.dataset.clientState = "polling";
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
      root.dataset.clientState = "polling";
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
    root.dataset.clientState = "unauthorized";
    await connection.stop();
    return;
  }

  if (!response.ok) {
    root.dataset.clientState = "polling";
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
  stateElement.hidden = false;
  stateElement.textContent = stateText(snapshot);

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
