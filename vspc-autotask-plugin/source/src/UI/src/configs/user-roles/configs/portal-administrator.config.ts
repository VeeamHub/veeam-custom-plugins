import { UserRole } from '@veeam-vspc/shared/vspc/models/web-ui';

import type { UserConfigData } from '@veeam-vspc/shared/vspc/core';

import { allRoutes } from '../routes';
import { RouteIds } from '../../enums';
import { AppPortalLayout } from '../../../views/components/AppPortalLayout';

export const portalAdministratorRoleConfig: UserConfigData<typeof UserRole.PortalAdministrator, any> = {
    role: UserRole.PortalAdministrator,
    routes: [
        { // External pages (separate window opened from the Catalog tile) — portal-shell layout
            path: '',
            index: true,
            component: AppPortalLayout,
            items: [
                allRoutes[RouteIds.PluginStatus],
                allRoutes[RouteIds.Companies],
                allRoutes[RouteIds.Ticketing],
                allRoutes[RouteIds.Billing],
                allRoutes[RouteIds.Activity],
            ],
        },
        allRoutes[RouteIds.Internal], // Internal page rendered inside the VSPC portal shell
    ],
    sections: [],
};
