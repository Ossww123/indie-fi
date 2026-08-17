import { getRequiredElement } from "../../shared/dom";
import {
  DirectMediaManager,
  readPositiveInteger,
} from "../../shared/direct-media-manager";

const root = getRequiredElement<HTMLElement>("[data-profile-media]");
const profileUserId = readPositiveInteger(
  root.dataset.profileUserId,
  "profile user id",
);
const maximumFileBytes = readPositiveInteger(
  root.dataset.maxFileBytes,
  "maximum file bytes",
);

new DirectMediaManager({
  root,
  targetKind: "Profile",
  targetId: profileUserId,
  maximumFileBytes,
  attachmentKinds: ["ProfileImage"],
}).initialize();
