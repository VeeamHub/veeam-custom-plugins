/**
 * Core module configuration for the Autotask PSA plugin
 * (based on the Veeam Service Provider Console Plugin Template).
 */

import type { PluginCoreModuleConfig } from '@veeam-vspc/shared/vspc/core-modules';
import { enLocale } from '../languages';
import { getPortalHomeUrl } from '@veeam-vspc/shared/vspc/helpers';
import { CompanyPrefixes, VeeamVspcProductPrefixes } from '@veeam-vspc/shared/vspc/enums';
import { UserRole } from '@veeam-vspc/shared/vspc/models/web-ui';
import React from 'react';

import { autotaskLogoWhite } from '../../images/autotask-logo';

export const coreModuleConfig: PluginCoreModuleConfig = {
    restPrefix: '/api/v3',
    baseUrl: '/uiapi',
    companyPrefix: CompanyPrefixes.VeeamVspc,
    productPrefix: 'AutotaskPsa' as VeeamVspcProductPrefixes,
    getLoginPath: (returnUrl?: string) => getPortalHomeUrl('/config/plugins')(returnUrl),
    configuratorConfig: {
        allRolesPaths: {
            // Administrative plugin: portal administrators only (backend enforces roles as well).
            [UserRole.PortalAdministrator]: () => (
                import('../user-roles/configs/portal-administrator.config').then(file => file.portalAdministratorRoleConfig)
            ),
        },
    },
    langConfig: {
        allLangPaths: {
            en: (): Promise<typeof enLocale> => import('../languages/locales/en.locale').then(file => file.enLocale),
        },
    },
    fileTransportConfig: {
        authorizationCodeProps: {
            queryKey: 'x-authorization-code',
        },
    },
    defaultAppData: {
        // Autotask mark (white monochrome) instead of the Veeam product logo, so the plugin's
        // own pages are branded for the integration they configure.
        portalLogo: <img src={autotaskLogoWhite} alt='Autotask PSA' style={{ width: 24, height: 24, display: 'block' }} />,
        fixedPortalName: 'Autotask PSA',
        defaultPortalName: 'Autotask PSA',
    },
    isDevPanelAllowedInDevMode: false,
};
