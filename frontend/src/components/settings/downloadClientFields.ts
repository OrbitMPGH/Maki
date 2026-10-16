import { useLingui } from '@lingui/react/macro'
import { useDownloadClientKinds } from '../../api/hooks'
import type { ConnectionField } from '../ConnectionSettingsCard'

/** The download client form: a kind select, with the username field and URL placeholder following the chosen kind. */
export function useDownloadClientFields(): (values: Record<string, string | null>) => ConnectionField[] {
  const { t } = useLingui()
  const { data: kinds } = useDownloadClientKinds()

  return (values) => {
    const selected = kinds?.find((k) => k.kind === values.kind)
    return [
      {
        key: 'kind',
        label: t`Client`,
        options: (kinds ?? []).map((k) => ({ value: k.kind, label: k.name })),
      },
      { key: 'url', label: t`URL`, placeholder: selected?.defaultUrl },
      ...(selected?.usesUsername === false ? [] : [{ key: 'username', label: t`Username` }]),
      { key: 'password', label: t`Password`, secret: true },
      {
        key: 'label',
        label: t`Label`,
        placeholder: 'maki',
        description: t`Category or label given to torrents Maki adds. Defaults to maki. Deluge lowercases it and allows only letters, digits, dot, dash and underscore.`,
      },
      { key: 'pathMapFrom', label: t`Path as the download client reports it`, placeholder: t`/downloads (optional)` },
      { key: 'pathMapTo', label: t`Path as Maki sees it`, placeholder: t`Z:\\downloads (optional)` },
    ]
  }
}
