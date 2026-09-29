import React from 'react';
import { VspcAppRouteItem } from '@veeam-vspc/shared/components';
import { RouteLevels } from '@veeam-vspc/shared/vspc/stores';
import { Bell, BuildingApp, Currency, Dashboard, ICON_SIZES, List } from '@veeam-vspc/shared/icons';

import { RouteIds } from '../../enums';
import { PluginStatusPage } from 'views/pages/PluginStatusPage';
import { CompaniesPage } from 'views/pages/CompaniesPage';
import { BillingPage } from 'views/pages/BillingPage';
import { TicketingPage } from 'views/pages/TicketingPage';
import { ActivityPage } from 'views/pages/ActivityPage';
import { InternalHome } from 'views/pages/InternalHome';

import type { MyPluginLang } from '../../languages';

// Navigation icons come from the shared icon set and deliberately match the icons used for
// the same concepts on the Plugin Status page (Companies/Ticketing/Billing), so the left
// nav and the feature list read as the same vocabulary.
const pluginStatusNavIcon = (): React.ReactElement => <Dashboard size={ICON_SIZES.m} />;
const companiesNavIcon = (): React.ReactElement => <BuildingApp size={ICON_SIZES.m} />;
const ticketingNavIcon = (): React.ReactElement => <Bell size={ICON_SIZES.m} />;
const billingNavIcon = (): React.ReactElement => <Currency size={ICON_SIZES.m} />;
const activityNavIcon = (): React.ReactElement => <List size={ICON_SIZES.m} />;

export const PluginStatusPageRoute = {
    path: '',
    exact: true,
    index: true,
    component: PluginStatusPage,
    navIcon: pluginStatusNavIcon,
    level: RouteLevels.NavMenuItem,
    navNameKey: 'PLUGIN_STATUS' as MyPluginLang['PLUGIN_STATUS'],
};

export const CompaniesPageRoute = {
    path: 'companies',
    exact: true,
    component: CompaniesPage,
    navIcon: companiesNavIcon,
    level: RouteLevels.NavMenuItem,
    navNameKey: 'COMPANIES' as MyPluginLang['COMPANIES'],
};

export const TicketingPageRoute = {
    path: 'ticketing',
    exact: true,
    component: TicketingPage,
    navIcon: ticketingNavIcon,
    level: RouteLevels.NavMenuItem,
    navNameKey: 'TICKETING' as MyPluginLang['TICKETING'],
};

export const BillingPageRoute = {
    path: 'billing',
    exact: true,
    component: BillingPage,
    navIcon: billingNavIcon,
    level: RouteLevels.NavMenuItem,
    navNameKey: 'BILLING' as MyPluginLang['BILLING'],
};

export const ActivityPageRoute = {
    path: 'activity',
    exact: true,
    component: ActivityPage,
    navIcon: activityNavIcon,
    level: RouteLevels.NavMenuItem,
    navNameKey: 'ACTIVITY' as MyPluginLang['ACTIVITY'],
};

export const InternalPageRoute = {
    path: 'autotaskPsa', // matches page-config.json/internals[0].location.pagePath
    exact: true,
    component: InternalHome,
    navNameKey: 'PLUGIN_STATUS' as MyPluginLang['PLUGIN_STATUS'],
};

export const allRoutes: Record<RouteIds, VspcAppRouteItem> = {
    [RouteIds.PluginStatus]: PluginStatusPageRoute,
    [RouteIds.Companies]: CompaniesPageRoute,
    [RouteIds.Ticketing]: TicketingPageRoute,
    [RouteIds.Billing]: BillingPageRoute,
    [RouteIds.Activity]: ActivityPageRoute,
    [RouteIds.Internal]: InternalPageRoute,
};
