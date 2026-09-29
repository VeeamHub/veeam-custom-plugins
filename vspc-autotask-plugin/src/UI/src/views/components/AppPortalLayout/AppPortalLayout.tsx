/**
 * Copyright © Veeam Software Group GmbH.
 */

import React from 'react';
import { observer } from 'mobx-react-lite';
import { PortalLayout } from '@veeam-vspc/shared/components';

import type { VspcAppRouteItem } from '@veeam-vspc/shared/components';

import { AppPortalBodyLayout } from '../AppPortalBodyLayout';

export interface AppPortalLayoutProps {
    currentPath?: string;
    items: VspcAppRouteItem[];
}

export const AppPortalLayout: React.FC<AppPortalLayoutProps> = observer(({ currentPath, items }) => (
    <PortalLayout currentPath={currentPath}>
        <AppPortalBodyLayout
            currentPath={currentPath}
            items={items}
        />
    </PortalLayout>
));
