import React, { useCallback, useRef, useState } from 'react';
import {
    BasicFilterKit,
    Grid,
    GridSortDirections,
    NoteBar,
    NOTEBAR_STATUS,
    SearchKit,
    SPACE_FILL,
    STACK_DIRECTION,
    STACK_GAP,
    StackView,
    TabBar,
    Text,
    TOOLBAR_ITEM_TYPE,
    Toolbar,
    useGlobalLang,
    useGlobalServices,
} from '@veeam-vspc/shared/components';
import {
    ArrowSyncCircle,
    CheckmarkCircle,
    Clock,
    DismissCircle,
    ICON_SIZES,
    Power,
    Prohibited,
    Settings,
} from '@veeam-vspc/shared/icons';

import type { GridColumnProps } from '@veeam-vspc/shared/components';
import type { MyPluginLang } from 'configs/languages';

import { pluginApi } from 'core/pluginApi';
import { useFeatureToggles } from 'core/features';
import { GridFilterBar, useGridFilters } from 'views/components/GridFilterBar';
import type { GridFilterApi } from 'views/components/GridFilterBar';
import type { AlarmRuleRow, PollSummary, TicketLinkRow } from 'core/apiTypes';
import { TicketSettingsDialog } from 'views/components/TicketSettingsDialog';

/** Alarm ticketing status, mirroring the CWM plugin's enabled/disabled status filter. */
enum AlarmStatus {
    Enabled = 'enabled',
    Disabled = 'disabled',
}

/** Ticket lifecycle states, exactly as the backend serializes TicketLinkState. */
const TICKET_STATES = ['Pending', 'Open', 'Closed', 'Cancelled', 'Error'] as const;

// ------------------------------------------------------------------ Alarms tab

const AlarmsTab: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const { notificationService } = useGlobalServices();
    const gridApi = useRef<GridFilterApi>();
    const { apply, applyDebounced } = useGridFilters(gridApi);
    const [nameSearch, setNameSearch] = useState('');
    const [statusFilter, setStatusFilter] = useState<AlarmStatus[]>([]);
    const [reloadKey, setReloadKey] = useState(0);
    const [lastSync, setLastSync] = useState<string | null>(null);
    const [selected, setSelected] = useState<AlarmRuleRow[]>([]);
    const [settingsOpen, setSettingsOpen] = useState(false);
    const [busy, setBusy] = useState(false);

    const reload = (): void => setReloadKey(k => k + 1);

    const loadAlarms = useCallback(async (requestParams?: {
        filter?: { alarmName?: string; alarmStatus?: AlarmStatus[] };
    }): Promise<{ data: AlarmRuleRow[]; meta: { pagingInfo: { total: number } } }> => {
        try {
            const all = await pluginApi<AlarmRuleRow[]>('/api/ticketing/alarms');

            const nameFilter = (requestParams?.filter?.alarmName ?? '').trim().toLowerCase();
            const statusFilter = requestParams?.filter?.alarmStatus ?? [];
            const rows = all.filter((row) => {
                if (nameFilter && !(row.name ?? '').toLowerCase().includes(nameFilter)) return false;
                if (statusFilter.length > 0
                    && !statusFilter.includes(row.enabled ? AlarmStatus.Enabled : AlarmStatus.Disabled)) return false;
                return true;
            });
            return { data: rows, meta: { pagingInfo: { total: rows.length } } };
        } catch (err) {
            notificationService.error(lang.ERROR, (err as Error).message);
            return { data: [], meta: { pagingInfo: { total: 0 } } };
        }
    }, [lang, notificationService]);

    const setEnabled = (enabled: boolean): void => {
        if (selected.length === 0) return;
        setBusy(true);
        pluginApi('/api/ticketing/alarms/enable', { body: { uids: selected.map(a => a.uid), enabled } })
            .then(reload)
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    const refreshData = (): void => {
        setBusy(true);
        pluginApi<{ count: number }>('/api/ticketing/alarms/refresh', { body: {} })
            .then((result) => {
                notificationService.info(lang.SUCCESS, `${result.count} ${lang.ALARM_TEMPLATES_LOADED}`);
                setLastSync(new Date().toISOString());
                reload();
            })
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    const statusCell = (row: AlarmRuleRow): React.ReactElement => (
        <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.s} style={{ alignItems: 'center' }}>
            {row.enabled ? <Power size={ICON_SIZES.m} /> : <DismissCircle size={ICON_SIZES.m} />}
            <Text>{row.enabled ? lang.ENABLED : lang.DISABLED}</Text>
        </StackView>
    );

    const columns: GridColumnProps<AlarmRuleRow>[] = [
        { id: 'status', title: lang.STATUS, cell: statusCell, width: 150 },
        { id: 'name', title: lang.ALARM_NAME, cell: row => row.name ?? row.uid },
        { id: 'objectType', title: lang.OBJECT_TYPE, cell: row => row.category ?? '-', width: 260 },
    ];

    return (
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m} spaceFill={SPACE_FILL.all} style={{ height: '100%' }}>
            {/* Search/filter controls in their own card; the action toolbar stays in the grid. */}
            <GridFilterBar>
                <SearchKit
                    value={nameSearch}
                    placeholder={lang.ALARM_NAME}
                    onChange={(value: string) => {
                        setNameSearch(value);
                        applyDebounced({ alarmName: value });
                    }}
                    onSearch={(value: string) => apply({ alarmName: value })}
                    onClear={() => {
                        setNameSearch('');
                        apply({ alarmName: '' });
                    }}
                />
                <BasicFilterKit
                    label={lang.STATUS_FILTER}
                    hasAllButton
                    textAllButton={lang.ALL}
                    value={statusFilter}
                    buttons={[
                        { title: lang.ENABLED, value: AlarmStatus.Enabled, icon: <Power size={ICON_SIZES.m} /> },
                        { title: lang.DISABLED, value: AlarmStatus.Disabled, icon: <DismissCircle size={ICON_SIZES.m} /> },
                    ]}
                    onChange={(items?: AlarmStatus[]) => {
                        const flat = (items ?? []).flat() as AlarmStatus[];
                        setStatusFilter(flat);
                        apply({ alarmStatus: flat });
                    }}
                />
            </GridFilterBar>

            <Grid
                api={gridApi}
                key={reloadKey}
                defaultColumnMinWidth={100}
                initialState={{ sort: { direction: GridSortDirections.Asc, key: 'status' } }}
                columns={columns}
                data={loadAlarms}
                toggleable={false}
                selection={{ field: 'uid', checkbox: true, multiple: true }}
                onSelectionChange={(rows: AlarmRuleRow[]) => setSelected(rows)}
                toolbars={[
                    () => (
                        <Toolbar
                            isLastToolbar
                            items={[
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.ENABLE,
                                    iconSrc: <Power size={ICON_SIZES.m} />,
                                    disabled: busy || selected.length === 0,
                                    onClick: () => setEnabled(true),
                                },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.DISABLE,
                                    iconSrc: <DismissCircle size={ICON_SIZES.m} />,
                                    disabled: busy || selected.length === 0,
                                    onClick: () => setEnabled(false),
                                },
                                { type: TOOLBAR_ITEM_TYPE.separator },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.TICKET_SETTINGS_BUTTON,
                                    iconSrc: <Settings size={ICON_SIZES.m} />,
                                    disabled: busy,
                                    onClick: () => setSettingsOpen(true),
                                },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: `${lang.REFRESH_DATA} (${lang.LAST_SYNC} ${lastSync
                                        ? new Date(lastSync).toLocaleString()
                                        : lang.NEVER_SYNCED})`,
                                    iconSrc: <ArrowSyncCircle size={ICON_SIZES.m} />,
                                    disabled: busy,
                                    onClick: refreshData,
                                },
                            ]}
                        />
                    ),
                ]}
            />

            {settingsOpen && <TicketSettingsDialog onClose={() => setSettingsOpen(false)} />}
        </StackView>
    );
};

// --------------------------------------------------- Recent ticket activity tab

const TicketActivityTab: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const { notificationService } = useGlobalServices();
    const gridApi = useRef<GridFilterApi>();
    const { apply, applyDebounced } = useGridFilters(gridApi);
    const [search, setSearch] = useState('');
    const [stateFilter, setStateFilter] = useState<string[]>([]);
    const [reloadKey, setReloadKey] = useState(0);
    const [busy, setBusy] = useState(false);

    const reload = (): void => setReloadKey(k => k + 1);

    const loadLinks = useCallback(async (requestParams?: {
        filter?: { ticketSearch?: string; ticketState?: string[] };
    }): Promise<{ data: TicketLinkRow[]; meta: { pagingInfo: { total: number } } }> => {
        try {
            const all = await pluginApi<TicketLinkRow[]>('/api/ticketing/links?limit=200');

            const search = (requestParams?.filter?.ticketSearch ?? '').trim().toLowerCase();
            const stateFilter = requestParams?.filter?.ticketState ?? [];
            const rows = all.filter((row) => {
                if (search) {
                    const haystack = [row.company, row.alarm, row.objectName, row.ticketNumber]
                        .map(v => (v ?? '').toLowerCase());
                    if (!haystack.some(v => v.includes(search))) return false;
                }
                if (stateFilter.length > 0 && !stateFilter.includes(row.state)) return false;
                return true;
            });
            return { data: rows, meta: { pagingInfo: { total: rows.length } } };
        } catch (err) {
            notificationService.error(lang.ERROR, (err as Error).message);
            return { data: [], meta: { pagingInfo: { total: 0 } } };
        }
    }, [lang, notificationService]);

    const pollNow = (): void => {
        setBusy(true);
        pluginApi<PollSummary>('/api/ticketing/poll', { body: {} })
            .then((result) => {
                notificationService.info(lang.SUCCESS, result.message
                    ?? `${result.created} ${lang.POLL_CREATED}, ${result.closed} ${lang.POLL_CLOSED}, ${result.errors} ${lang.POLL_ERRORS}`);
                reload();
            })
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    const stateIcon = (state: string): React.ReactElement => {
        switch (state) {
            case 'Open': return <Power size={ICON_SIZES.m} />;
            case 'Closed': return <CheckmarkCircle size={ICON_SIZES.m} />;
            case 'Pending': return <Clock size={ICON_SIZES.m} />;
            case 'Cancelled': return <Prohibited size={ICON_SIZES.m} />;
            default: return <DismissCircle size={ICON_SIZES.m} />;
        }
    };

    const columns: GridColumnProps<TicketLinkRow>[] = [
        {
            id: 'state',
            title: lang.STATUS,
            cell: row => (
                <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.s} style={{ alignItems: 'center' }}>
                    {stateIcon(row.state)}
                    <Text>{row.state}</Text>
                </StackView>
            ),
            width: 140,
        },
        {
            id: 'ticket',
            title: lang.TICKET,
            cell: row => row.ticketNumber ?? (row.ticketId != null ? `#${row.ticketId}` : '-'),
            width: 150,
        },
        { id: 'company', title: lang.VSPC_COMPANY, cell: row => row.company ?? '-' },
        { id: 'alarm', title: lang.ALARM, cell: row => row.alarm ?? '-' },
        { id: 'objectName', title: lang.OBJECT, cell: row => row.objectName ?? '-' },
        { id: 'updatedAt', title: lang.UPDATED, cell: row => new Date(row.updatedAt).toLocaleString(), width: 170 },
        { id: 'info', title: lang.MESSAGE, cell: row => row.error ?? row.lastStatus ?? '' },
    ];

    return (
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m} spaceFill={SPACE_FILL.all} style={{ height: '100%' }}>
            {/* Search/filter controls in their own card; the action toolbar stays in the grid. */}
            <GridFilterBar>
                <SearchKit
                    value={search}
                    placeholder={lang.SEARCH_TICKETS}
                    onChange={(value: string) => {
                        setSearch(value);
                        applyDebounced({ ticketSearch: value });
                    }}
                    onSearch={(value: string) => apply({ ticketSearch: value })}
                    onClear={() => {
                        setSearch('');
                        apply({ ticketSearch: '' });
                    }}
                />
                <BasicFilterKit
                    label={lang.STATUS_FILTER}
                    hasAllButton
                    textAllButton={lang.ALL}
                    value={stateFilter}
                    buttons={TICKET_STATES.map(state => ({
                        title: state,
                        value: state as string,
                        icon: stateIcon(state),
                    }))}
                    onChange={(items?: string[]) => {
                        const flat = (items ?? []).flat() as string[];
                        setStateFilter(flat);
                        apply({ ticketState: flat });
                    }}
                />
            </GridFilterBar>

            <Grid
                api={gridApi}
                key={reloadKey}
                defaultColumnMinWidth={90}
                initialState={{ sort: { direction: GridSortDirections.Desc, key: 'updatedAt' } }}
                columns={columns}
                data={loadLinks}
                toggleable={false}
                toolbars={[
                    () => (
                        <Toolbar
                            isLastToolbar
                            items={[
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.POLL_ALARMS_NOW,
                                    iconSrc: <ArrowSyncCircle size={ICON_SIZES.m} />,
                                    disabled: busy,
                                    onClick: pollNow,
                                },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.REFRESH,
                                    iconSrc: <ArrowSyncCircle size={ICON_SIZES.m} />,
                                    disabled: busy,
                                    onClick: reload,
                                },
                            ]}
                        />
                    ),
                ]}
            />
        </StackView>
    );
};

// ------------------------------------------------------------------------ page

export const TicketingPage: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const features = useFeatureToggles();
    const [active, setActive] = useState(0);

    const tabs = [
        { title: lang.ALARMS_TAB, render: () => <AlarmsTab /> },
        { title: lang.TICKET_ACTIVITY_TAB, render: () => <TicketActivityTab /> },
    ];

    return (
        // No page title here: the portal shell already renders it from the route definition.
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m} spaceFill={SPACE_FILL.all} style={{ height: '100%', padding: 16 }}>
            {features && !features.ticketing && (
                <NoteBar status={NOTEBAR_STATUS.info}>
                    <Text>{lang.FEATURE_DISABLED_HINT.replace('{0}', lang.TICKETING)}</Text>
                </NoteBar>
            )}

            <TabBar
                items={tabs.map((tab, index) => ({
                    title: tab.title,
                    active: index === active,
                    onClick: () => setActive(index),
                }))}
            />

            <StackView spaceFill={SPACE_FILL.all} style={{ flex: 1, minHeight: 0 }}>
                {tabs[active].render()}
            </StackView>
        </StackView>
    );
};
