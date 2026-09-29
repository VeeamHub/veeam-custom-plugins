import React, { useState } from 'react';
import {
    ComboboxKit,
    Grid,
    GridSortDirections,
    SPACE_FILL,
    StackView,
    TOOLBAR_ITEM_TYPE,
    Toolbar,
    useGlobalLang,
} from '@veeam-vspc/shared/components';

import type { GridColumnProps } from '@veeam-vspc/shared/components';
import type { MyPluginLang } from 'configs/languages';

import { pluginApi } from 'core/pluginApi';
import type { ActivityRow } from 'core/apiTypes';

export const ActivityPage: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const [area, setArea] = useState('');
    const [level, setLevel] = useState('');
    const [reloadKey, setReloadKey] = useState(0);

    const columns: GridColumnProps<ActivityRow>[] = [
        { id: 'ts', title: lang.TIME, cell: row => new Date(row.ts).toLocaleString(), width: 170 },
        { id: 'level', title: lang.LEVEL, cell: row => row.level, width: 90 },
        { id: 'area', title: lang.AREA, cell: row => row.area, width: 110 },
        { id: 'message', title: lang.MESSAGE, cell: row => row.detail ? `${row.message} — ${row.detail}` : row.message },
    ];

    return (
        <StackView spaceFill={SPACE_FILL.all} style={{ height: '100%' }}>
            <Grid
                key={`${area}|${level}|${reloadKey}`}
                defaultColumnMinWidth={80}
                initialState={{ sort: { direction: GridSortDirections.Desc, key: 'ts' } }}
                columns={columns}
                data={() => pluginApi<ActivityRow[]>(
                    `/api/activity?limit=300${area ? `&area=${area}` : ''}${level ? `&level=${level}` : ''}`,
                ).then(data => ({ data, meta: { pagingInfo: { total: data.length } } }))}
                toggleable={false}
                toolbars={[
                    () => (
                        <Toolbar
                            isLastToolbar
                            items={[
                                {
                                    type: TOOLBAR_ITEM_TYPE.customControl,
                                    render: () => (
                                        <ComboboxKit
                                            data={[
                                                { id: '', name: lang.ALL_AREAS },
                                                { id: 'connection', name: lang.AREA_CONNECTION },
                                                { id: 'companies', name: lang.AREA_COMPANIES },
                                                { id: 'billing', name: lang.AREA_BILLING },
                                                { id: 'ticketing', name: lang.AREA_TICKETING },
                                                { id: 'system', name: lang.AREA_SYSTEM },
                                            ]}
                                            value={area}
                                            valueGetter={option => option.id}
                                            textGetter={option => option.name}
                                            onChange={(value: string) => setArea(value)}
                                        />
                                    ),
                                },
                                {
                                    type: TOOLBAR_ITEM_TYPE.customControl,
                                    render: () => (
                                        <ComboboxKit
                                            data={[
                                                { id: '', name: lang.ALL_LEVELS },
                                                { id: 'info', name: 'info' },
                                                { id: 'warn', name: 'warn' },
                                                { id: 'error', name: 'error' },
                                            ]}
                                            value={level}
                                            valueGetter={option => option.id}
                                            textGetter={option => option.name}
                                            onChange={(value: string) => setLevel(value)}
                                        />
                                    ),
                                },
                                {
                                    type: TOOLBAR_ITEM_TYPE.button,
                                    text: lang.REFRESH,
                                    onClick: () => setReloadKey(k => k + 1),
                                },
                            ]}
                        />
                    ),
                ]}
            />
        </StackView>
    );
};
