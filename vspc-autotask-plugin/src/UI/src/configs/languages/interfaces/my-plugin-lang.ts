/**
 * Copyright © Veeam Software Group GmbH.
 */

import type { BaseLang } from '@veeam-vspc/shared/vspc/interfaces';

import type { enLocale } from '../locales';

export type MyPluginLang = typeof enLocale & BaseLang;
