Licenses
=================================================

이 폴더에는 배포 패키지에 포함된 제3자 소프트웨어의 라이선스 원문이 들어 있습니다.

현재 빌드에서 자동으로 포함되는 파일
  - LICENSE_LGPL_21.txt      Open CASCADE Technology (LGPL 2.1)
  - OCCT_LGPL_EXCEPTION.txt  Open CASCADE 예외 조항

추가로 넣어야 하는 파일
  tools\deploy\assets\Licenses\ 폴더에 파일을 넣으면 패키지 빌드 시 이 폴더로
  함께 복사됩니다. 어떤 항목이 필요한지는 THIRD-PARTY-NOTICES.txt 의
  "배포 담당자 체크리스트" 를 참고하세요.

  예:
     FTL.TXT              FreeType License
     FIPL.txt             FreeImage Public License
     Apache-2.0.txt       oneTBB
     BSD-2-Clause.txt     jemalloc
     BSD-3-Clause.txt     OpenVR
     BSL-1.0.txt          Clipper2
     MIT-netDxf.txt       netDxf
     LGPL-2.1-FFmpeg.txt  FFmpeg

이 파일은 build-info.json 과 함께 배포되며, 패키지 무결성 목록(manifest.sha256)에
포함됩니다.
