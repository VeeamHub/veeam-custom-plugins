/**
 * Copyright © Veeam Software Group GmbH.
 */

import React from 'react';
import { Outlet } from 'react-router-dom';
import {
    GoBackButton,
    NotificationBarWrapper,
    PortalBodyLayout,
    PortalHeader,
    useGlobalContext,
    useGlobalLang,
    useGlobalAppData,
} from '@veeam-vspc/shared/components';
import { NavigationMenuState } from '@veeam-vspc/shared/generic/enums';
import { observer } from 'mobx-react-lite';

import type { VspcAppRouteItem } from '@veeam-vspc/shared/components';
import type { MyPluginLang } from 'configs/languages';

export interface AppPortalBodyLayoutProps {
    currentPath?: string;
    items: VspcAppRouteItem[];
}

export const AppPortalBodyLayout: React.FC<AppPortalBodyLayoutProps> = observer(({ currentPath, items }) => {
    const lang = useGlobalLang<MyPluginLang>();
    const globalAppData = useGlobalAppData();
    const { getLoginPath } = useGlobalContext();

    // Every page is always navigable. Feature toggles govern what the plugin *does*
    // (company sync, ticket creation, billing runs) — not which pages you can open. A page
    // whose feature is off shows an inline hint pointing at the Plugin Status page.
    return (
        <PortalBodyLayout
            currentPath={currentPath}
            items={items}
            header={<PortalHeader />}
            portalSideMenuProps={{
                renderHeader: () => (
                    <GoBackButton
                        text={lang.GO_BACK}
                        isCompact={globalAppData.navigationMenuState === NavigationMenuState.Collapsed}
                        onClick={(event) => {
                            event.preventDefault();
                            window.location.assign(getLoginPath());
                        }}
                    />
                ),
            }}
        >
            <NotificationBarWrapper>
                <Outlet key={currentPath} />
            </NotificationBarWrapper>
        </PortalBodyLayout>
    );
});
