import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
    Button,
    CheckboxKit,
    ComboboxKit,
    createSearchableControl,
    DIALOG_SIZE,
    Grid,
    GridSortDirections,
    LinkButton,
    Modal,
    NoteBar,
    NOTEBAR_STATUS,
    PortalSpinner,
    SearchKit,
    SPACE_FILL,
    STACK_DIRECTION,
    STACK_GAP,
    StackView,
    TabBar,
    Text,
    TEXT_SIZE,
    TEXT_WEIGHT,
    TextInputKit,
    TOOLBAR_ITEM_TYPE,
    Toolbar,
    ToggleKit,
    VspcDialog,
    useGlobalLang,
    useGlobalServices,
} from '@veeam-vspc/shared/components';
import { ArrowSyncCircle, Filter, ICON_SIZES } from '@veeam-vspc/shared/icons';

import type { GridColumnProps } from '@veeam-vspc/shared/components';
import type { MyPluginLang } from 'configs/languages';

import { pluginApi } from 'core/pluginApi';
import { useFeatureToggles } from 'core/features';
import { GridFilterBar, useGridFilters } from 'views/components/GridFilterBar';
import type { GridFilterApi } from 'views/components/GridFilterBar';
import type {
    AtBillingCodeOption,
    AtContractOption,
    AtServiceOption,
    BillingLine,
    BillingSettings,
    CompanyBillingRow,
    PicklistValue,
    ServiceMappingRow,
    SubscriptionPlanOption,
} from 'core/apiTypes';

const MODES = ['Skip', 'Existing', 'CreateNew'] as const;

const fieldRowStyle: React.CSSProperties = {
    display: 'grid',
    gridTemplateColumns: 'minmax(240px, 380px) minmax(220px, 360px) auto',
    gap: 16,
    alignItems: 'center',
};

const sectionStyle: React.CSSProperties = { padding: '16px 24px' };

// ---------------------------------------------------------------- Contracts tab

const AgreementsTab: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const { notificationService } = useGlobalServices();
    const gridApi = useRef<GridFilterApi>();
    const { apply, applyDebounced } = useGridFilters(gridApi);
    const [search, setSearch] = useState('');
    const [reloadKey, setReloadKey] = useState(0);
    const [rows, setRows] = useState<CompanyBillingRow[]>([]);
    const [services, setServices] = useState<ServiceMappingRow[]>([]);
    const [contracts, setContracts] = useState<AtContractOption[]>([]);
    const [editRow, setEditRow] = useState<CompanyBillingRow | null>(null);
    const [draftContractId, setDraftContractId] = useState<number | null>(null);
    const [draftServices, setDraftServices] = useState<string[]>([]);
    const [lastSync, setLastSync] = useState<string | null>(null);
    const [results, setResults] = useState<BillingLine[] | null>(null);
    const [busy, setBusy] = useState(false);

    const reload = (): void => setReloadKey(k => k + 1);

    useEffect(() => {
        pluginApi<ServiceMappingRow[]>('/api/billing/services').then(setServices).catch(() => setServices([]));
    }, []);

    const loadCompanies = useCallback(async (requestParams?: {
        filter?: { companyName?: string };
    }): Promise<{ data: CompanyBillingRow[]; meta: { pagingInfo: { total: number } } }> => {
        try {
            const all = await pluginApi<CompanyBillingRow[]>('/api/billing/companies');
            setRows(all);
            const nameFilter = (requestParams?.filter?.companyName ?? '').trim().toLowerCase();
            const rows = nameFilter
                ? all.filter(r => r.name.toLowerCase().includes(nameFilter)
                    || (r.atCompanyName ?? '').toLowerCase().includes(nameFilter))
                : all;
            return { data: rows, meta: { pagingInfo: { total: rows.length } } };
        } catch (err) {
            notificationService.error(lang.ERROR, (err as Error).message);
            return { data: [], meta: { pagingInfo: { total: 0 } } };
        }
    }, [lang, notificationService]);

    const openEditor = (row: CompanyBillingRow): void => {
        setEditRow(row);
        setDraftContractId(row.contractId ?? null);
        setDraftServices([...row.enabledServices]);
        setContracts([]);
        // NOTE: company id 0 is VALID — Autotask reserves it for the account's own company
        // record, and querying it returns that company's real contracts. Only a missing or
        // negative id indicates a broken mapping, so never test this id for truthiness.
        if (row.atCompanyId == null || row.atCompanyId < 0) {
            notificationService.error(lang.ERROR, `${lang.INVALID_AT_COMPANY} (${row.name})`);
            return;
        }
        pluginApi<AtContractOption[]>(`/api/autotask/contracts?companyId=${row.atCompanyId}`)
            .then(setContracts)
            .catch(err => notificationService.error(lang.ERROR, err.message));
    };

    const saveCompany = (): void => {
        if (!editRow || draftContractId == null) {
            notificationService.error(lang.WARNING, lang.SELECT_CONTRACT);
            return;
        }
        const contractName = contracts.find(c => c.id === draftContractId)?.name ?? editRow.contractName ?? '';
        setBusy(true);
        pluginApi('/api/billing/companies', {
            body: {
                vspcUid: editRow.vspcUid,
                contractId: draftContractId,
                contractName,
                enabledServices: draftServices,
            },
        })
            .then(() => {
                notificationService.info(lang.SUCCESS, lang.BILLING_SAVED);
                setEditRow(null);
                reload();
            })
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    const runSync = (dryRun: boolean): void => {
        if (!dryRun && !window.confirm(lang.SYNC_CONFIRMATION)) return;
        setBusy(true);
        pluginApi<BillingLine[]>(dryRun ? '/api/billing/preview' : '/api/billing/run', { body: {} })
            .then((lines) => {
                setResults(lines);
                setLastSync(new Date().toISOString());
            })
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    const billableServices = useMemo(() => services.filter(s => s.mode !== 'Skip'), [services]);

    const columns: GridColumnProps<CompanyBillingRow>[] = [
        { id: 'name', title: lang.VSPC_COMPANY, cell: row => row.name },
        {
            id: 'agreement',
            title: lang.AGREEMENT,
            cell: row => (row.contractId != null ? `${row.contractName ?? lang.CONTRACT} (#${row.contractId})` : '-'),
        },
        {
            id: 'enabledProducts',
            title: lang.ENABLED_PRODUCTS,
            cell: row => (row.enabledServices.length > 0 ? String(row.enabledServices.length) : '-'),
            width: 190,
        },
        {
            id: 'actions',
            title: '',
            width: 210,
            cell: row => (
                <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.m}>
                    <LinkButton onClick={() => openEditor(row)}>{lang.CONFIGURE}</LinkButton>
                    {row.contractId != null && (
                        <LinkButton
                            onClick={() => {
                                if (!window.confirm(lang.DISABLE_BILLING_CONFIRMATION)) return;
                                pluginApi('/api/billing/companies/remove', { body: { vspcUid: row.vspcUid } })
                                    .then(reload)
                                    .catch(err => notificationService.error(lang.ERROR, err.message));
                            }}
                        >
                            {lang.DISABLE_BILLING}
                        </LinkButton>
                    )}
                </StackView>
            ),
        },
    ];

    const resultColumns: GridColumnProps<BillingLine>[] = [
        { id: 'company', title: lang.VSPC_COMPANY, cell: row => row.vspcCompanyName },
        { id: 'service', title: lang.SERVICE, cell: row => row.serviceName },
        { id: 'current', title: lang.CURRENT, cell: row => row.currentUnits, width: 90 },
        { id: 'measured', title: lang.MEASURED, cell: row => row.desiredUnits, width: 90 },
        { id: 'delta', title: lang.DELTA, cell: row => (row.delta > 0 ? `+${row.delta}` : row.delta), width: 80 },
        { id: 'status', title: lang.STATUS, cell: row => row.status, width: 110 },
        { id: 'detail', title: lang.MESSAGE, cell: row => row.detail ?? '' },
    ];

    return (
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m} spaceFill={SPACE_FILL.all} style={{ height: '100%' }}>
            {/* Search in its own card; the action toolbar stays inside the grid. */}
            <GridFilterBar>
                <SearchKit
                    value={search}
                    placeholder={lang.COMPANY_NAME}
                    onChange={(value: string) => {
                        setSearch(value);
                        applyDebounced({ companyName: value });
                    }}
                    onSearch={(value: string) => apply({ companyName: value })}
                    onClear={() => {
                        setSearch('');
                        apply({ companyName: '' });
                    }}
                />
            </GridFilterBar>

            <Grid
                api={gridApi}
                key={reloadKey}
                defaultColumnMinWidth={100}
                initialState={{ sort: { direction: GridSortDirections.Asc, key: 'name' } }}
                columns={columns}
                data={loadCompanies}
                toggleable={false}
                toolbars={[
                    () => (
                        <Toolbar
                            isLastToolbar
                            items={[
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: `${lang.REFRESH_DATA} (${lang.LAST_SYNC} ${lastSync
                                        ? new Date(lastSync).toLocaleString()
                                        : lang.NEVER_SYNCED})`,
                                    iconSrc: <ArrowSyncCircle size={ICON_SIZES.m} />,
                                    disabled: busy,
                                    onClick: reload,
                                },
                                { type: TOOLBAR_ITEM_TYPE.separator },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.PREVIEW_DRY_RUN,
                                    disabled: busy,
                                    onClick: () => runSync(true),
                                },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.SYNC_NOW_BUTTON,
                                    disabled: busy,
                                    onClick: () => runSync(false),
                                },
                                { type: TOOLBAR_ITEM_TYPE.fillSpace },
                                {
                                    // Placeholder: CWM's advanced filter panel has no equivalent here.
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.FILTER_NONE,
                                    iconSrc: <Filter size={ICON_SIZES.m} />,
                                    disabled: true,
                                    onClick: () => undefined,
                                },
                            ]}
                        />
                    ),
                ]}
            />

            {results && (
                <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.s} style={{ height: 320 }}>
                    <Text weight={TEXT_WEIGHT.bold}>{lang.SYNCHRONIZATION}</Text>
                    <Grid
                        defaultColumnMinWidth={80}
                        initialState={{ sort: { direction: GridSortDirections.Asc, key: 'company' } }}
                        columns={resultColumns}
                        data={() => Promise.resolve({ data: results, meta: { pagingInfo: { total: results.length } } })}
                        toggleable={false}
                    />
                </StackView>
            )}

            {editRow && (
                <Modal
                    isShown
                    onRequestClose={() => setEditRow(null)}
                    render={() => (
                        <VspcDialog
                            header={`${lang.COMPANY_BILLING}: ${editRow.name}`}
                            size={{ width: DIALOG_SIZE.m, height: DIALOG_SIZE.auto }}
                            limitDimensions
                            movable={false}
                            loading={busy}
                            onRequestClose={() => setEditRow(null)}
                            actions={[
                                { text: lang.SAVE, onClick: saveCompany, disabled: busy },
                                { text: lang.CANCEL, onClick: () => setEditRow(null), disabled: busy },
                            ]}
                        >
                            <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m}>
                                <Text>{`${editRow.atCompanyName} (#${editRow.atCompanyId})`}</Text>
                                <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.xs}>
                                    <Text>{lang.CONTRACT}</Text>
                                    <ComboboxKit
                                        data={contracts}
                                        value={draftContractId}
                                        valueGetter={c => c.id}
                                        textGetter={c => `${c.name} (#${c.id})`}
                                        controlRenderer={createSearchableControl()}
                                        accessibility={{ isSearchable: true }}
                                        onChange={(id: number) => setDraftContractId(id)}
                                    />
                                    {contracts.length === 0 && <Text size={TEXT_SIZE.s}>{lang.NO_CONTRACTS_FOUND}</Text>}
                                </StackView>
                                <Text weight={TEXT_WEIGHT.bold}>{lang.ENABLED_PRODUCTS}</Text>
                                <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.xs}>
                                    {billableServices.length === 0 && <Text size={TEXT_SIZE.s}>{lang.SERVICE_MAPPING_HINT}</Text>}
                                    {billableServices.map(service => (
                                        <CheckboxKit
                                            key={service.key}
                                            checked={draftServices.includes(service.key)}
                                            onChange={(checked: boolean) => setDraftServices(current => (checked
                                                ? [...current, service.key]
                                                : current.filter(k => k !== service.key)))}
                                        >
                                            {service.name}
                                        </CheckboxKit>
                                    ))}
                                </StackView>
                            </StackView>
                        </VspcDialog>
                    )}
                />
            )}
        </StackView>
    );
};

// ---------------------------------------------------- Product Mapping tab

const ProductMappingTab: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const { notificationService } = useGlobalServices();
    const [services, setServices] = useState<ServiceMappingRow[] | null>(null);
    const [settings, setSettings] = useState<BillingSettings | null>(null);
    const [plans, setPlans] = useState<SubscriptionPlanOption[]>([]);
    const [atServices, setAtServices] = useState<AtServiceOption[]>([]);
    const [billingCodes, setBillingCodes] = useState<AtBillingCodeOption[]>([]);
    const [periodTypes, setPeriodTypes] = useState<PicklistValue[]>([]);
    const [openGroups, setOpenGroups] = useState<Record<string, boolean>>({});
    const [busy, setBusy] = useState(false);

    useEffect(() => {
        pluginApi<ServiceMappingRow[]>('/api/billing/services').then(setServices).catch(() => setServices([]));
        pluginApi<BillingSettings>('/api/settings/billing').then(setSettings)
            .catch(err => notificationService.error(lang.ERROR, err.message));
        pluginApi<SubscriptionPlanOption[]>('/api/vspc/subscriptionplans').then(setPlans).catch(() => setPlans([]));
        pluginApi<AtServiceOption[]>('/api/autotask/services').then(setAtServices).catch(() => setAtServices([]));
        pluginApi<AtBillingCodeOption[]>('/api/autotask/billingcodes').then(setBillingCodes).catch(() => setBillingCodes([]));
        pluginApi<{ periodTypes: PicklistValue[] }>('/api/autotask/servicefields')
            .then(r => setPeriodTypes(r.periodTypes)).catch(() => setPeriodTypes([]));
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    // Grouped before any early return — hooks must not sit behind a conditional.
    const groups = useMemo(() => {
        const byGroup = new Map<string, ServiceMappingRow[]>();
        (services ?? []).forEach((row) => {
            const list = byGroup.get(row.group) ?? [];
            list.push(row);
            byGroup.set(row.group, list);
        });
        return Array.from(byGroup.entries());
    }, [services]);

    if (!services || !settings) return <PortalSpinner delayTime={300} />;

    const updateService = (key: string, patch: Partial<ServiceMappingRow>): void =>
        setServices(rows => (rows ?? []).map(row => (row.key === key ? { ...row, ...patch } : row)));

    const saveService = (row: ServiceMappingRow): void => {
        pluginApi('/api/billing/services', {
            body: {
                serviceKey: row.key,
                mode: row.mode,
                atServiceId: row.atServiceId ?? null,
                atServiceName: row.atServiceName ?? `Veeam - ${row.name}`,
                unitPrice: row.unitPrice ?? 0,
                billingCodeId: row.billingCodeId ?? null,
                periodType: row.periodType ?? null,
            },
        })
            .then(() => notificationService.info(lang.SUCCESS, lang.SERVICE_MAPPING_SAVED))
            .catch(err => notificationService.error(lang.ERROR, err.message));
    };

    const applySettings = (): void => {
        setBusy(true);
        pluginApi('/api/settings/billing', { body: settings })
            .then(() => notificationService.info(lang.SUCCESS, lang.SETTINGS_SAVED))
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    return (
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m} style={sectionStyle}>
            <Text>{lang.PRODUCT_MAPPING_INTRO}</Text>

            <NoteBar status={NOTEBAR_STATUS.info}>
                <Text>{lang.PRODUCT_MAPPING_NOTE}</Text>
            </NoteBar>

            <div style={fieldRowStyle}>
                <Text>{lang.SUBSCRIPTION_PLAN_LABEL}</Text>
                <ComboboxKit
                    data={plans}
                    value={settings.subscriptionPlanUid ?? null}
                    valueGetter={plan => plan.uid}
                    textGetter={plan => (plan.currency ? `${plan.name} (${plan.currency})` : plan.name)}
                    placeholder={lang.SELECT_SUBSCRIPTION_PLAN}
                    controlRenderer={createSearchableControl()}
                    accessibility={{ isSearchable: true }}
                    disabled={plans.length === 0}
                    onChange={(uid: string) => setSettings({ ...settings, subscriptionPlanUid: uid })}
                />
                <span />
            </div>

            <div style={fieldRowStyle}>
                {/* Placeholder: storage units are fixed by the service catalog in this integration. */}
                <Text style={{ opacity: 0.55 }}>{lang.STORAGE_UNIT_LABEL}</Text>
                <ComboboxKit
                    data={[{ id: 'TB', name: 'TB' }]}
                    value={'TB'}
                    valueGetter={option => option.id}
                    textGetter={option => option.name}
                    disabled
                    onChange={() => undefined}
                />
                <span />
            </div>

            <div style={fieldRowStyle}>
                <Text>{lang.ANCHOR_DAY}</Text>
                <TextInputKit
                    value={String(settings.anchorDayOfMonth)}
                    onlyNumbers
                    onChange={(value: string) => setSettings({ ...settings, anchorDayOfMonth: Number(value) || 1 })}
                />
                <span />
            </div>

            <div style={fieldRowStyle}>
                <Text>{lang.SYNC_HOUR}</Text>
                <TextInputKit
                    value={String(settings.syncHourUtc)}
                    onlyNumbers
                    onChange={(value: string) => setSettings({ ...settings, syncHourUtc: Number(value) || 0 })}
                />
                <span />
            </div>

            <div style={fieldRowStyle}>
                <Text>{lang.DEFAULT_BILLING_CODE}</Text>
                <ComboboxKit
                    data={billingCodes}
                    value={settings.defaultBillingCodeId ?? null}
                    valueGetter={code => code.id}
                    textGetter={code => code.name}
                    controlRenderer={createSearchableControl()}
                    accessibility={{ isSearchable: true }}
                    disabled={billingCodes.length === 0}
                    onChange={(id: number) => setSettings({ ...settings, defaultBillingCodeId: id })}
                />
                <span />
            </div>

            <StackView direction={STACK_DIRECTION.row}>
                <Button disabled={busy} onClick={applySettings}>{lang.APPLY}</Button>
            </StackView>

            {/* Service sections: one collapsible block per service group, matching the
                "enable one of the sections below" model of the reference plugin. */}
            {groups.map(([group, rows]) => {
                const enabledCount = rows.filter(r => r.mode !== 'Skip').length;
                const isOpen = openGroups[group] ?? false;
                return (
                    <StackView key={group} direction={STACK_DIRECTION.column} gap={STACK_GAP.s}
                        style={{ borderTop: '1px solid rgba(128,128,128,0.25)', paddingTop: 12 }}
                    >
                        <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.m} style={{ alignItems: 'center' }}>
                            <ToggleKit
                                value={isOpen || enabledCount > 0}
                                onChange={(value: boolean) => setOpenGroups(current => ({ ...current, [group]: value }))}
                            />
                            <Text weight={TEXT_WEIGHT.bold}>{group}</Text>
                            <Text size={TEXT_SIZE.s}>{`${enabledCount} / ${rows.length}`}</Text>
                        </StackView>

                        {(isOpen || enabledCount > 0) && rows.map(row => (
                            <StackView key={row.key} direction={STACK_DIRECTION.row} gap={STACK_GAP.s}
                                style={{ alignItems: 'center', flexWrap: 'wrap', paddingLeft: 48 }}
                            >
                                <Text style={{ width: 240 }}>{`${row.name} (${row.unit})`}</Text>
                                <ComboboxKit
                                    data={[...MODES]}
                                    value={row.mode}
                                    valueGetter={mode => mode}
                                    textGetter={mode => ({
                                        Skip: lang.DO_NOT_BILL,
                                        Existing: lang.EXISTING_SERVICE,
                                        CreateNew: lang.CREATE_NEW_SERVICE,
                                    }[mode])}
                                    onChange={(mode: typeof MODES[number]) => updateService(row.key, { mode })}
                                />
                                {row.mode === 'Existing' && (
                                    <ComboboxKit
                                        data={atServices}
                                        value={row.atServiceId ?? null}
                                        valueGetter={s => s.id}
                                        textGetter={s => s.name}
                                        controlRenderer={createSearchableControl()}
                                        accessibility={{ isSearchable: true }}
                                        onChange={(id: number) => updateService(row.key, { atServiceId: id })}
                                    />
                                )}
                                {row.mode === 'CreateNew' && (
                                    <>
                                        <TextInputKit
                                            value={row.atServiceName ?? `Veeam - ${row.name}`}
                                            onChange={(value: string) => updateService(row.key, { atServiceName: value })}
                                            placeholder={lang.NEW_SERVICE_NAME}
                                        />
                                        <TextInputKit
                                            value={String(row.unitPrice ?? '')}
                                            onlyNumbers
                                            onChange={(value: string) => updateService(row.key, { unitPrice: parseFloat(value) || 0 })}
                                            placeholder={lang.UNIT_PRICE}
                                        />
                                        <ComboboxKit
                                            data={periodTypes}
                                            value={row.periodType != null ? String(row.periodType) : null}
                                            valueGetter={p => p.value}
                                            textGetter={p => p.label}
                                            onChange={(value: string) => updateService(row.key, { periodType: Number(value) })}
                                        />
                                    </>
                                )}
                                <Button onClick={() => saveService(row)}>{lang.SAVE}</Button>
                            </StackView>
                        ))}
                    </StackView>
                );
            })}
        </StackView>
    );
};

// ------------------------------------------------------------------ page

export const BillingPage: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const features = useFeatureToggles();
    const [active, setActive] = useState(0);

    const tabs = [
        { title: lang.AGREEMENTS, render: () => <AgreementsTab /> },
        { title: lang.PRODUCT_MAPPING_TAB, render: () => <ProductMappingTab /> },
    ];

    return (
        // No page title here: the portal shell already renders it from the route definition.
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m} spaceFill={SPACE_FILL.all} style={{ height: '100%', padding: 16 }}>
            {features && !features.billing && (
                <NoteBar status={NOTEBAR_STATUS.info}>
                    <Text>{lang.FEATURE_DISABLED_HINT.replace('{0}', lang.BILLING)}</Text>
                </NoteBar>
            )}

            <TabBar
                items={tabs.map((tab, index) => ({
                    title: tab.title,
                    active: index === active,
                    onClick: () => setActive(index),
                }))}
            />

            <StackView spaceFill={SPACE_FILL.all} style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
                {tabs[active].render()}
            </StackView>
        </StackView>
    );
};
