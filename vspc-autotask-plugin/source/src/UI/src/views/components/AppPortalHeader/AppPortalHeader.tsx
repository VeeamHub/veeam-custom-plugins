/**
 * Copyright © Veeam Software Group GmbH.
 */

import React from 'react';
import {
    APP_BAR_ITEM_TYPE,
    PortalHeader,
    useGlobalAppData,
    useGlobalContext,
    useGlobalServices,
    PortalHeaderExternalButton,
} from '@veeam-vspc/shared/components';
import { observer } from 'mobx-react-lite';

import type { AppBarItemProps } from '@veeam-vspc/shared/components';

export const AppPortalHeader: React.FC = observer(() => {
    const { externalPluginsStore, prefixes } = useGlobalContext();
    const { transportService } = useGlobalServices();
    const appDataStore = useGlobalAppData();

    const portalHeaderItems: AppBarItemProps[] = [
        {
            type: APP_BAR_ITEM_TYPE.fillSpace,
        },
    ];

    if (externalPluginsStore?.portalHeaderPlugins.length > 0) {
        externalPluginsStore.portalHeaderPlugins.forEach(({ index, ...restPortalHeaderPluginProps }) => {
            delete restPortalHeaderPluginProps.text;

            portalHeaderItems.splice(
                index - 1,
                0,
                {
                    type: APP_BAR_ITEM_TYPE.custom,
                    render: () => (
                        <PortalHeaderExternalButton
                            {...restPortalHeaderPluginProps}
                            externalPortalHeaderPluginApi={{
                                services: {
                                    transportService,
                                },
                                globalAppData: {
                                    portalUser: appDataStore.portalUser,
                                    theme: appDataStore.theme,
                                    uiModeConfig: appDataStore.uiModeConfig,
                                    companyPrefix: prefixes.companyPrefix,
                                },
                            }}
                        />
                    ),
                },
                { type: APP_BAR_ITEM_TYPE.separator },
            );
        });
    }

    return (
        <PortalHeader
            logo={appDataStore.portalLogo}
            title={appDataStore.portalName}
            items={portalHeaderItems}
        />
    );
});
