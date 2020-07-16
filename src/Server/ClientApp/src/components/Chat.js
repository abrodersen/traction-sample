import React, { Component } from 'react'
import { ChatInput } from './ChatInput'
import ChatMessage from './ChatMessage'

function getWsUri(id) {
  let scheme = window.location.protocol == 'https:' ? 'wss' : 'ws';
  let wsUri = scheme + '://' + window.location.host + '/api/rooms/' + id;
  return wsUri;
};


export class Chat extends Component {
  constructor(props) {
    super(props);
    const { id } = props.match.params;

    this.room = id;

    this.state = {
      name: 'Me',
      messages: [],
    };
  };

  resetWebsocket() {
    let wsUri = getWsUri(this.room);
    console.log('connecting to ' + wsUri);
    this.ws = new WebSocket(wsUri);

    this.ws.onopen = () => {
      // on connecting, do nothing but log it to the console
      console.log('connected')
    }

    this.ws.onmessage = evt => {
      // on receiving a message, add it to the list of messages
      const message = JSON.parse(evt.data);
      if (message.type == 'chat') {
        this.addMessage(message);
      }
    }

    this.ws.onclose = () => {
      console.log('disconnected')
      // automatically try to reconnect on connection loss
      this.resetWebsocket();
    }
  }

  sendKeepalive() {
    if (this.ws && this.ws.readyState == WebSocket.OPEN) {
      let msg = { type: 'keepalive' };
      let data = JSON.stringify(msg);
      this.ws.send(data);
    }
  }

  componentDidMount() {
    this.resetWebsocket();
    setInterval(this.sendKeepalive.bind(this), 5000);
  }

  addMessage = message =>
    this.setState(state => ({ messages: [message, ...state.messages] }))

  submitMessage = messageString => {
    const message = { type: 'chat', name: this.state.name, message: messageString }
    let data = JSON.stringify(message);
    this.ws.send(data)
    this.addMessage(message)
  }

  render() {
    return (
      <div>
        <label htmlFor="name">
          Name:&nbsp;
          <input
            type="text"
            id={'name'}
            placeholder={'Enter your name...'}
            value={this.state.name}
            onChange={e => this.setState({ name: e.target.value })}
          />
        </label>
        <ChatInput
          ws={this.ws}
          onSubmitMessage={messageString => this.submitMessage(messageString)}
        />
        {this.state.messages.map((message, index) =>
          <ChatMessage
            key={index}
            message={message.message}
            name={message.name}
          />,
        )}
      </div>
    )
  }
}
