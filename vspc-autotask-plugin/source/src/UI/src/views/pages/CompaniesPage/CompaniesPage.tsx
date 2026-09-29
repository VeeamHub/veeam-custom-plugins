import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
    BasicFilterKit,
    ComboboxKit,
    createSearchableControl,
    DIALOG_SIZE,
    Grid,
    GridSortDirections,
    LinkButton,
    Modal,
    NoteBar,
    NOTEBAR_STATUS,
    SearchKit,
    SPACE_FILL,
    STACK_DIRECTION,
    STACK_GAP,
    StackView,
    Text,
    TEXT_WEIGHT,
    TOOLBAR_ITEM_TYPE,
    Toolbar,
    VspcDialog,
    useGlobalLang,
    useGlobalServices,
} from '@veeam-vspc/shared/components';
import {
    ArrowSwap,
    ArrowSyncCircle,
    Clock,
    DismissCircle,
    ICON_SIZES,
    QuestionCircle,
} from '@veeam-vspc/shared/icons';

import type { GridColumnProps } from '@veeam-vspc/shared/components';
import type { MyPluginLang } from 'configs/languages';

import { pluginApi } from 'core/pluginApi';
import { useFeatureToggles } from 'core/features';
import { GridFilterBar, useGridFilters } from 'views/components/GridFilterBar';
import type { GridFilterApi } from 'views/components/GridFilterBar';
import type { AutomapResponse, CompaniesResponse, MatchSuggestion, PicklistValue, VspcCompanyRow } from 'core/apiTypes';

/**
 * Mapping status values, mirroring the status filter of the ConnectWise Manage plugin.
 * Our mapping is applied synchronously, so rows are only ever Mapped or Unmapped —
 * InProgress and Error exist to keep the filter set identical to the reference.
 */
enum MappingStatus {
    Mapped = 'mapped',
    Unmapped = 'unmapped',
    InProgress = 'inProgress',
    Error = 'error',
}

const statusOf = (row: VspcCompanyRow): MappingStatus =>
    row.mapping ? MappingStatus.Mapped : MappingStatus.Unmapped;

export const CompaniesPage: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const { notificationService } = useGlobalServices();
    const features = useFeatureToggles();
    const gridApi = useRef<GridFilterApi>();
    const { apply, applyDebounced } = useGridFilters(gridApi);
    const [nameSearch, setNameSearch] = useState('');
    const [statusFilter, setStatusFilter] = useState<MappingStatus[]>([]);
    const [reloadKey, setReloadKey] = useState(0);
    const [companies, setCompanies] = useState<CompaniesResponse | null>(null);
    const [companyTypes, setCompanyTypes] = useState<PicklistValue[]>([]);
    const [selected, setSelected] = useState<VspcCompanyRow[]>([]);
    const [mapTarget, setMapTarget] = useState<VspcCompanyRow | null>(null);
    const [mapAtId, setMapAtId] = useState<number | null>(null);
    const [suggestions, setSuggestions] = useState<MatchSuggestion[] | null>(null);
    const [busy, setBusy] = useState(false);

    const reload = (): void => setReloadKey(k => k + 1);

    // Autotask company-type labels for the Type column/filter (instance-configurable picklist).
    useEffect(() => {
        pluginApi<PicklistValue[]>('/api/autotask/companytypes')
            .then(setCompanyTypes)
            .catch(() => undefined); // Autotask not connected yet — Type simply renders empty
    }, []);

    // The grid owns the fetch (a data function closing over state returns stale/empty rows)
    // and applies the filter-bar values the grid passes in through requestParams.
    const loadCompanies = useCallback(async (requestParams?: {
        filter?: { companyName?: string; mappingStatus?: MappingStatus[]; companyType?: string[] };
    }): Promise<{ data: VspcCompanyRow[]; meta: { pagingInfo: { total: number } } }> => {
        try {
            let data = await pluginApi<CompaniesResponse>('/api/companies');
            if (data.needsRefresh) {
                await pluginApi('/api/companies/refresh', { body: {} });
                data = await pluginApi<CompaniesResponse>('/api/companies');
            }
            setCompanies(data);

            const nameFilter = (requestParams?.filter?.companyName ?? '').trim().toLowerCase();
            const statusFilter = requestParams?.filter?.mappingStatus ?? [];
            const typeFilter = requestParams?.filter?.companyType ?? [];
            const rows = (data.vspcCompanies ?? []).filter((row) => {
                if (nameFilter && !row.name.toLowerCase().includes(nameFilter)
                    && !(row.mapping?.atName ?? '').toLowerCase().includes(nameFilter)) return false;
                if (statusFilter.length > 0 && !statusFilter.includes(statusOf(row))) return false;
                if (typeFilter.length > 0 && !typeFilter.includes(String(row.mapping?.atType ?? ''))) return false;
                return true;
            });
            return { data: rows, meta: { pagingInfo: { total: rows.length } } };
        } catch (err) {
            notificationService.error(lang.ERROR, (err as Error).message);
            return { data: [], meta: { pagingInfo: { total: 0 } } };
        }
    }, [lang, notificationService]);

    const unmappedAt = useMemo(
        () => (companies?.atCompanies ?? []).filter(c => !c.mapped),
        [companies],
    );
    const typeLabel = useCallback((value?: number | null): string => {
        if (value == null) return '-';
        return companyTypes.find(t => t.value === String(value))?.label ?? String(value);
    }, [companyTypes]);

    // ---- actions (all driven from the toolbar, against the grid selection) ----

    const mapCompany = (vspcUid: string, atId: number): void => {
        pluginApi('/api/companies/map', { body: { vspcUid, atId } })
            .then(() => {
                setMapTarget(null);
                setSuggestions(null);
                reload();
            })
            .catch(err => notificationService.error(lang.ERROR, err.message));
    };

    const removeMapping = (): void => {
        const mapped = selected.filter(row => row.mapping);
        if (mapped.length === 0) return;
        if (!window.confirm(lang.UNMAP_CONFIRMATION)) return;
        setBusy(true);
        Promise.all(mapped.map(row => pluginApi('/api/companies/unmap', { body: { vspcUid: row.uid } })))
            .then(reload)
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    const mapCompaniesAutomatically = (): void => {
        setBusy(true);
        pluginApi<AutomapResponse>('/api/companies/automap', { body: { apply: true } })
            .then((result) => {
                notificationService.info(lang.SUCCESS,
                    `${result.autoMapped} ${lang.AUTO_MAP_RESULT} ${result.suggestions.length} ${lang.NEED_REVIEW}`);
                setSuggestions(result.suggestions);
                reload();
            })
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    const refreshData = (): void => {
        setBusy(true);
        pluginApi<{ vspc: number; autotask: number }>('/api/companies/refresh', { body: {} })
            .then((result) => {
                notificationService.info(lang.SUCCESS, `${result.vspc} VSPC / ${result.autotask} Autotask`);
                reload();
            })
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    // ---- grid ----

    const statusCell = (row: VspcCompanyRow): React.ReactElement => {
        const status = statusOf(row);
        const [icon, text] = status === MappingStatus.Mapped
            ? [<ArrowSwap key='i' size={ICON_SIZES.m} />, lang.MAPPED]
            : [<QuestionCircle key='i' size={ICON_SIZES.m} />, lang.UNMAPPED];
        return (
            <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.s} style={{ alignItems: 'center' }}>
                {icon}
                <Text>{text}</Text>
            </StackView>
        );
    };

    // Column order follows the ConnectWise Manage plugin: PSA company, PSA company id, type,
    // then the Veeam Service Provider Console company it is mapped to, then site.
    const columns: GridColumnProps<VspcCompanyRow>[] = [
        { id: 'status', title: lang.STATUS, cell: statusCell, width: 150 },
        {
            id: 'atCompany',
            title: lang.AUTOTASK_COMPANY,
            cell: row => (row.mapping
                ? `${row.mapping.atName}${row.mapping.auto ? ` (${lang.AUTO_MAPPED_BADGE})` : ''}`
                : '-'),
        },
        { id: 'atCompanyId', title: lang.AUTOTASK_COMPANY_ID, cell: row => (row.mapping ? String(row.mapping.atId) : '-'), width: 190 },
        { id: 'type', title: lang.TYPE, cell: row => typeLabel(row.mapping?.atType), width: 140 },
        { id: 'vspcCompany', title: lang.VSPC_COMPANY_FULL, cell: row => row.name },
    ];

    const singleUnmappedSelected = selected.length === 1 && !selected[0]?.mapping;
    const hasMappedSelection = selected.some(row => row.mapping);

    return (
        // No page title here: the portal shell already renders it from the route definition.
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m} spaceFill={SPACE_FILL.all} style={{ height: '100%', padding: 16 }}>
            {features && !features.companies && (
                <NoteBar status={NOTEBAR_STATUS.info}>
                    <Text>{lang.FEATURE_DISABLED_HINT.replace('{0}', lang.COMPANIES)}</Text>
                </NoteBar>
            )}

            {/* Search/filter controls live in their own card; the action toolbar below stays
                inside the grid so it reads as one surface with the rows. */}
            <GridFilterBar>
                <SearchKit
                    value={nameSearch}
                    placeholder={lang.COMPANY_NAME}
                    onChange={(value: string) => {
                        setNameSearch(value);
                        applyDebounced({ companyName: value });
                    }}
                    onSearch={(value: string) => apply({ companyName: value })}
                    onClear={() => {
                        setNameSearch('');
                        apply({ companyName: '' });
                    }}
                />

                <BasicFilterKit
                    label={lang.STATUS_FILTER}
                    hasAllButton
                    textAllButton={lang.ALL}
                    value={statusFilter}
                    buttons={[
                        { title: lang.MAPPED, value: MappingStatus.Mapped, icon: <ArrowSwap size={ICON_SIZES.m} /> },
                        { title: lang.UNMAPPED, value: MappingStatus.Unmapped, icon: <QuestionCircle size={ICON_SIZES.m} /> },
                        { title: lang.IN_PROGRESS, value: MappingStatus.InProgress, icon: <Clock size={ICON_SIZES.m} /> },
                        { title: lang.ERROR_STATUS, value: MappingStatus.Error, icon: <DismissCircle size={ICON_SIZES.m} /> },
                    ]}
                    onChange={(items?: MappingStatus[]) => {
                        const flat = (items ?? []).flat() as MappingStatus[];
                        setStatusFilter(flat);
                        apply({ mappingStatus: flat });
                    }}
                />
            </GridFilterBar>

            <Grid
                api={gridApi}
                key={reloadKey}
                defaultColumnMinWidth={100}
                initialState={{ sort: { direction: GridSortDirections.Asc, key: 'status' } }}
                columns={columns}
                data={loadCompanies}
                toggleable={false}
                selection={{ field: 'uid', checkbox: true, multiple: true }}
                onSelectionChange={(rows: VspcCompanyRow[]) => setSelected(rows)}
                toolbars={[
                    // Action row: every mapping operation runs from here against the grid
                    // selection — there are deliberately no per-row action links.
                    () => (
                        <Toolbar
                            isLastToolbar
                            items={[
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.MAP_TO,
                                    iconSrc: <ArrowSwap size={ICON_SIZES.m} />,
                                    disabled: busy || !singleUnmappedSelected,
                                    onClick: () => {
                                        setMapAtId(null);
                                        setMapTarget(selected[0] ?? null);
                                    },
                                },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.UNMAP,
                                    iconSrc: <DismissCircle size={ICON_SIZES.m} />,
                                    disabled: busy || !hasMappedSelection,
                                    onClick: removeMapping,
                                },
                                { type: TOOLBAR_ITEM_TYPE.separator },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.MAP_COMPANIES_AUTOMATICALLY,
                                    iconSrc: <ArrowSyncCircle size={ICON_SIZES.m} />,
                                    disabled: busy,
                                    onClick: mapCompaniesAutomatically,
                                },
                                { type: TOOLBAR_ITEM_TYPE.separator },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: `${lang.REFRESH_DATA} (${lang.LAST_SYNC} ${companies?.fetchedAt
                                        ? new Date(companies.fetchedAt).toLocaleString()
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

            {mapTarget && (
                <Modal
                    isShown
                    onRequestClose={() => setMapTarget(null)}
                    render={() => (
                        <VspcDialog
                            header={`${lang.MAP_COMPANY_TITLE}: ${mapTarget.name}`}
                            size={{ width: DIALOG_SIZE.s, height: DIALOG_SIZE.auto }}
                            limitDimensions
                            movable={false}
                            onRequestClose={() => setMapTarget(null)}
                            actions={[
                                { text: lang.CANCEL, onClick: () => setMapTarget(null) },
                                {
                                    text: lang.MAP_COMPANY_TITLE,
                                    disabled: mapAtId == null,
                                    onClick: () => mapAtId != null && mapCompany(mapTarget.uid, mapAtId),
                                },
                            ]}
                        >
                            <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m}>
                                <Text>{lang.SELECT_AUTOTASK_COMPANY}</Text>
                                <ComboboxKit
                                    data={unmappedAt}
                                    value={mapAtId}
                                    valueGetter={item => item.id}
                                    textGetter={item => `${item.name} (#${item.id})`}
                                    controlRenderer={createSearchableControl()}
                                    accessibility={{ isSearchable: true }}
                                    onChange={(value: number) => setMapAtId(value)}
                                />
                            </StackView>
                        </VspcDialog>
                    )}
                />
            )}

            {suggestions && (
                <Modal
                    isShown
                    onRequestClose={() => setSuggestions(null)}
                    render={() => (
                        <VspcDialog
                            header={lang.SUGGESTED_MATCHES}
                            size={{ width: DIALOG_SIZE.m, height: DIALOG_SIZE.auto }}
                            limitDimensions
                            movable={false}
                            onRequestClose={() => setSuggestions(null)}
                            actions={[{ text: lang.CLOSE, onClick: () => setSuggestions(null) }]}
                        >
                            <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m}>
                                {suggestions.length === 0 && (
                                    <NoteBar status={NOTEBAR_STATUS.info}>
                                        <Text>{lang.NO_SUGGESTIONS}</Text>
                                    </NoteBar>
                                )}
                                {suggestions.map(suggestion => (
                                    <StackView key={suggestion.vspcCompanyUid} direction={STACK_DIRECTION.column} gap={STACK_GAP.s}>
                                        <Text weight={TEXT_WEIGHT.bold}>{suggestion.vspcCompanyName}</Text>
                                        <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.s} style={{ flexWrap: 'wrap' }}>
                                            {suggestion.candidates.map(candidate => (
                                                <LinkButton
                                                    key={candidate.atCompanyId}
                                                    onClick={() => mapCompany(suggestion.vspcCompanyUid, candidate.atCompanyId)}
                                                >
                                                    {`${candidate.atCompanyName} (${Math.round(candidate.score * 100)}% ${lang.MATCH})`}
                                                </LinkButton>
                                            ))}
                                        </StackView>
                                    </StackView>
                                ))}
                            </StackView>
                        </VspcDialog>
                    )}
                />
            )}
        </StackView>
    );
};
