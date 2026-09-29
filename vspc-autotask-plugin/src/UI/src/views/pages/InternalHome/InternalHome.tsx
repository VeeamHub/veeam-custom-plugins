import React, { useMemo, useState } from 'react';
import {
    SPACE_FILL,
    STACK_DIRECTION,
    STACK_GAP,
    StackView,
    TabBar,
    useGlobalLang,
} from '@veeam-vspc/shared/components';

import type { MyPluginLang } from 'configs/languages';

import { PluginStatusPage } from 'views/pages/PluginStatusPage';
import { CompaniesPage } from 'views/pages/CompaniesPage';
import { BillingPage } from 'views/pages/BillingPage';
import { TicketingPage } from 'views/pages/TicketingPage';
import { ActivityPage } from 'views/pages/ActivityPage';

interface PluginTab {
    id: string;
    title: string;
    render: () => React.ReactElement;
}

/**
 * The internal page rendered inside the VSPC portal shell (page-config pagePath):
 * the full plugin experience presented as native tabs, in the same order as the
 * ConnectWise Manage plugin's navigation. Every tab is always present — feature toggles
 * govern what the plugin does, not which pages are reachable; a page whose feature is off
 * shows an inline hint pointing at the Plugin Status page.
 */
export const InternalHome: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const [activeId, setActiveId] = useState('pluginStatus');

    const tabs = useMemo<PluginTab[]>(() => [
        { id: 'pluginStatus', title: lang.PLUGIN_STATUS, render: () => <PluginStatusPage /> },
        { id: 'companies', title: lang.COMPANIES, render: () => <CompaniesPage /> },
        { id: 'ticketing', title: lang.TICKETING, render: () => <TicketingPage /> },
        { id: 'billing', title: lang.BILLING, render: () => <BillingPage /> },
        { id: 'activity', title: lang.ACTIVITY, render: () => <ActivityPage /> },
    ], [lang]);

    const activeIndex = Math.max(0, tabs.findIndex(tab => tab.id === activeId));

    return (
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.s} spaceFill={SPACE_FILL.all} style={{ height: '100%' }}>
            <TabBar
                items={tabs.map((tab, index) => ({
                    title: tab.title,
                    active: index === activeIndex,
                    onClick: () => setActiveId(tab.id),
                }))}
            />
            <StackView spaceFill={SPACE_FILL.all} style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
                {tabs[activeIndex].render()}
            </StackView>
        </StackView>
    );
};
