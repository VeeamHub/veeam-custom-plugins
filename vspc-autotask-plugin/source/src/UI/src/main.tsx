/**
 * Copyright © Veeam Software Group GmbH.
 */

import '@veeam-vspc/shared/styles';
import React from 'react';
import { createRoot } from 'react-dom/client';
import { ProductSections } from '@veeam-vspc/shared/vspc/helpers';
import { AppRoot, VspcAppContainer } from '@veeam-vspc/shared/components';
import { PluginCoreModule } from '@veeam-vspc/shared/vspc/core-modules';

import type { AppRootProps } from '@veeam-vspc/shared/components';

import { coreModuleConfig } from './configs/core-module-config';

if (process.env.NODE_ENV === 'development') {
    localStorage.setItem(`${coreModuleConfig.companyPrefix}/${ProductSections.General}/${ProductSections.Token}`, JSON.stringify({
        /* eslint-disable @typescript-eslint/naming-convention */
        access_token: 'access_token',
        refresh_token: 'refresh_token',
        access_expires_in: Date.now() + (1000 * 60 * 60 * 24),
        refresh_expires_in: Date.now() + (1000 * 60 * 60 * 24),
    }));

    console.log('Added token for dev mode, without it will be forever reload');
}

const props: AppRootProps<PluginCoreModule> = {
    coreModule: new PluginCoreModule(coreModuleConfig),
};
const container = document.getElementById('root');
const root = createRoot(container);

root.render(
    <AppRoot {...props}>
        <VspcAppContainer />
    </AppRoot>
);
