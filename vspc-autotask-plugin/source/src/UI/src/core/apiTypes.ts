/**
 * Response shapes of the Autotask PSA plugin backend (camelCase JSON).
 */

export interface StatusResponse {
    vspc: { configured: boolean; baseUrl?: string };
    autotask: { configured: boolean; connected: boolean; error?: string; zoneUrl?: string };
    features: { companies: boolean; billing: boolean; ticketing: boolean };
    counts: {
        vspcCompanies: number;
        atCompanies: number;
        mappedCompanies: number;
        billingCompanies: number;
        enabledAlarms: number;
        openTickets: number;
        pendingTickets: number;
    };
    lastTicketPoll?: {
        at: string;
        swept: number;
        created: number;
        closed: number;
        notes: number;
        cancelled: number;
        errors: number;
        message?: string;
    };
    lastBillingRun?: string;
    snapshotAge?: string;
}

export interface CompaniesResponse {
    needsRefresh: boolean;
    fetchedAt?: string;
    vspcCompanies?: VspcCompanyRow[];
    atCompanies?: AtCompanyRow[];
}

export interface VspcCompanyRow {
    uid: string;
    name: string;
    status?: string;
    mapping?: { atId: number; atName: string; auto: boolean; atType?: number | null } | null;
}

export interface AtCompanyRow {
    id: number;
    name: string;
    mapped: boolean;
}

export interface MatchSuggestion {
    vspcCompanyUid: string;
    vspcCompanyName: string;
    candidates: { atCompanyId: number; atCompanyName: string; score: number; exact: boolean }[];
}

export interface AutomapResponse {
    autoMapped: number;
    suggestions: MatchSuggestion[];
}

export interface ServiceMappingRow {
    key: string;
    group: string;
    name: string;
    unit: string;
    aggregation: string;
    mode: 'Skip' | 'Existing' | 'CreateNew';
    atServiceId?: number | null;
    atServiceName?: string | null;
    unitPrice?: number | null;
    billingCodeId?: number | null;
    periodType?: number | null;
}

export interface CompanyBillingRow {
    vspcUid: string;
    name: string;
    atCompanyId: number;
    atCompanyName: string;
    contractId?: number | null;
    contractName?: string | null;
    enabledServices: string[];
}

export interface BillingLine {
    vspcCompanyUid: string;
    vspcCompanyName: string;
    serviceKey: string;
    serviceName: string;
    desiredUnits: number;
    currentUnits: number;
    delta: number;
    contractId?: number;
    serviceId?: number;
    status: string;
    detail?: string;
}

export interface BillingHistoryRow {
    id: number;
    ts: string;
    vspcCompanyUid: string;
    vspcCompanyName?: string;
    serviceKey: string;
    prevUnits?: number;
    newUnits?: number;
    delta?: number;
    effectiveDate?: string;
    dryRun: boolean;
    result?: string;
}

export interface AlarmRuleRow {
    uid: string;
    name?: string;
    category?: string;
    internalId?: number;
    enabled: boolean;
}

export interface TicketLinkRow {
    activeAlarmUid: string;
    company?: string;
    alarm?: string;
    objectName?: string;
    ticketId?: number;
    ticketNumber?: string;
    state: string;
    lastStatus?: string;
    repeatCount: number;
    dueAt?: string;
    error?: string;
    updatedAt: string;
}

export interface ActivityRow {
    id: number;
    ts: string;
    level: string;
    area: string;
    message: string;
    detail?: string;
}

export interface AutotaskSettings {
    username: string;
    integrationCode: string;
    hasSecret: boolean;
    zoneUrl: string;
}

export interface FeatureToggles {
    companies: boolean;
    billing: boolean;
    ticketing: boolean;
}

export interface TicketingSettings {
    queueId?: number | null;
    newStatusId?: number | null;
    completeStatusId?: number | null;
    warningPriorityId?: number | null;
    errorPriorityId?: number | null;
    sourceId?: number | null;
    ticketTypeId?: number | null;
    delayMinutes: number;
    dueHours: number;
    pollSeconds: number;
    closeTicketOnAlarmResolve: boolean;
    resolveAlarmOnTicketClose: boolean;
    noteOnRetrigger: boolean;
    acknowledgeClosesTicket: boolean;
}

export interface BillingSettings {
    subscriptionPlanUid?: string | null;
    anchorDayOfMonth: number;
    syncHourUtc: number;
    defaultPeriodType?: number | null;
    defaultBillingCodeId?: number | null;
}

export interface SubscriptionPlanOption {
    uid: string;
    name: string;
    currency?: string;
}

export interface PicklistValue {
    value: string;
    label: string;
    isDefault?: boolean;
}

export interface TicketPicklists {
    queues: PicklistValue[];
    statuses: PicklistValue[];
    priorities: PicklistValue[];
    sources: PicklistValue[];
    ticketTypes: PicklistValue[];
}

export interface AtServiceOption { id: number; name: string; unitPrice?: number }
export interface AtBillingCodeOption { id: number; name: string }
export interface AtContractOption { id: number; name: string; contractType?: number; status?: number }

export interface PollSummary {
    at: string;
    swept: number;
    created: number;
    closed: number;
    notes: number;
    cancelled: number;
    errors: number;
    message?: string;
}
