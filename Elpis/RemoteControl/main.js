
let $artist
let $title
let $cover
let $playButton
let $skipButton
let currentSong
let isPlaying = false

function periodicStatusUpdate() {
  fetch('/currentsong')
    .then((response) => response.json())
    .then((song) => {
      if (song) {
        if (currentSong !== song.SongDetailUrl) {
          currentSong = song.SongDetailUrl
          document.title = `${song.SongTitle} by ${song.Artist} - Elpis`
          $artist.innerText = song.Artist
          $title.innerText = song.SongTitle
          $cover.src = '/albumcover?' + Math.random()
        }
      } else {
        currentSong = null
        document.title = 'Elpis'
      }
    })

  fetch('/isplaying')
    .then((response) => response.text())
    .then((response) => {
      isPlaying = response == 'yes'
      togglePlayButton(!isPlaying)
    })
}

function togglePlayButton(paused) {
  if (paused)
    $playButton.classList.remove('active')
  else
    $playButton.classList.add('active')
}

function playButtonClick(event) {
  const url = isPlaying ? '/pause' : '/play'
  fetch(url)
    .then((repsonse) => {
      togglePlayButton(isPlaying)
      isPlaying = !isPlaying
    })

  event.preventDefault()
  return false
}

function skipButtonClick(event) {
  fetch('/next')
    .then((repsonse) => {
      periodicStatusUpdate()
    })

  event.preventDefault()
  return false
}

window.addEventListener('load', function(event) {
  $artist = document.querySelector('#artist')
  $title = document.querySelector('#title')
  $cover = document.querySelector('#cover')
  $playButton = document.querySelector('#play')
  $playButton.addEventListener('click', playButtonClick, false)
  $skipButton = document.querySelector('#skip')
  $skipButton.addEventListener('click', skipButtonClick, false)

  periodicStatusUpdate()
  setInterval(periodicStatusUpdate, 5000)
})
