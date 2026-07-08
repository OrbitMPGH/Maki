import {
  Badge,
  Button,
  Center,
  Group,
  Image,
  Loader,
  Stack,
  Text,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useNavigate, useParams } from 'react-router-dom'
import { useDeleteSeries, useSeriesDetail } from '../api/hooks'

export default function SeriesDetailPage() {
  const { id } = useParams()
  const navigate = useNavigate()
  const { data: series, isLoading } = useSeriesDetail(Number(id))
  const deleteSeries = useDeleteSeries()

  if (isLoading) {
    return (
      <Center py="xl">
        <Loader />
      </Center>
    )
  }

  if (!series) {
    return <Text c="red">Series not found.</Text>
  }

  return (
    <Stack>
      <Group align="flex-start" wrap="nowrap">
        {series.coverUrl && (
          <Image src={series.coverUrl} w={180} radius="md" alt={series.title} />
        )}
        <Stack gap="xs" style={{ flex: 1 }}>
          <Title order={2}>{series.title}</Title>
          <Group gap="xs">
            <Badge variant="light">{series.status}</Badge>
            {series.year && <Badge variant="outline">{series.year}</Badge>}
            {series.genres.slice(0, 6).map((g) => (
              <Badge key={g} variant="default" size="sm">
                {g}
              </Badge>
            ))}
          </Group>
          {series.authorStory && (
            <Text size="sm" c="dimmed">
              Story: {series.authorStory}
              {series.authorArt && series.authorArt !== series.authorStory
                ? ` · Art: ${series.authorArt}`
                : ''}
            </Text>
          )}
          <Text size="sm">{series.overview}</Text>
          <Group mt="sm">
            <Button
              variant="light"
              color="red"
              size="xs"
              loading={deleteSeries.isPending}
              onClick={() =>
                deleteSeries.mutate(
                  { id: series.id, deleteFiles: false },
                  {
                    onSuccess: () => {
                      notifications.show({ message: 'Series removed', color: 'green' })
                      navigate('/')
                    },
                  },
                )
              }
            >
              Remove from library
            </Button>
          </Group>
        </Stack>
      </Group>
      <Title order={4}>Chapters</Title>
      <Text c="dimmed" size="sm">
        Chapter list arrives with source support (M3).
      </Text>
    </Stack>
  )
}
