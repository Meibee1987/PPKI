import type { AuditFindingPage, FindingFilters } from "./audit-contract.ts";
import { findingsQuery } from "./audit-contract.ts";
import type { CanonicalAuditIdentity } from "./canonical-audit-identity.ts";

export type FindingRequestToken = Readonly<{ sequence: number; key: string }>;

export function findingRequestKey(identity: CanonicalAuditIdentity, filters: FindingFilters): string {
  return `${identity.auditId}:${identity.documentVersionId}?${findingsQuery(filters)}`;
}

export function createLatestFindingRequestGuard() {
  let sequence = 0;
  let active: FindingRequestToken | undefined;
  return {
    begin(key: string): FindingRequestToken {
      active = { sequence: ++sequence, key };
      return active;
    },
    isCurrent(token: FindingRequestToken): boolean {
      return active?.sequence === token.sequence && active.key === token.key;
    },
    cancel(token: FindingRequestToken): void {
      if (active?.sequence === token.sequence && active.key === token.key) active = undefined;
    },
  };
}

export function assertCanonicalFindingPage(
  identity: Pick<CanonicalAuditIdentity, "auditId" | "documentVersionId">,
  page: AuditFindingPage,
): AuditFindingPage {
  if (page.auditId !== identity.auditId || page.documentVersionId !== identity.documentVersionId)
    throw new Error("canonical-finding-page-lineage-invalid");
  return page;
}

export function hasFindingQuery(filters: FindingFilters): boolean {
  return Boolean(filters.severity || filters.fixMode || filters.disposition
    || filters.domain || filters.ruleCode || filters.validationKey || filters.search);
}
