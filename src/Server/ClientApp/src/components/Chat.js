import React, { Component } from 'react'
import { ChatInput } from './ChatInput'
import ChatMessage from './ChatMessage'

export class Chat extends Component {
  constructor(props) {
    super(props);
    const { id } = props.match.params;

    let scheme = window.location.protocol == 'https' ? 'wss' : 'ws';
    let wsUri = scheme + '://' + window.location.host + '/api/rooms/' + id;
    console.log('connecting to ' + wsUri);
    this.ws = new WebSocket(wsUri)

    this.state = {
      name: 'Me',
      messages: [],
    };
  };


  componentDidMount() {
    this.ws.onopen = () => {
      // on connecting, do nothing but log it to the console
      console.log('connected')
    }

    this.ws.onmessage = evt => {
      // on receiving a message, add it to the list of messages
      // const message = JSON.parse(evt.data)
      let msg = { name: 'anon', message: evt.data};
      this.addMessage(msg)
    }

    this.ws.onclose = () => {
      console.log('disconnected')
      // automatically try to reconnect on connection loss
      this.setState({
        ws: new WebSocket(URL),
      })
    }
  }

  addMessage = message =>
    this.setState(state => ({ messages: [message, ...state.messages] }))

  submitMessage = messageString => {
    const message = { name: this.state.name, message: messageString }
    this.ws.send(messageString)
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
